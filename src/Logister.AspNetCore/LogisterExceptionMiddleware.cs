using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Logister.AspNetCore;

public sealed class LogisterExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LogisterClient _client;
    private readonly LogisterAspNetCoreOptions _options;
    private readonly LogisterEventQueue _queue;
    private readonly ILogger<LogisterExceptionMiddleware> _logger;
    private readonly LogisterValueSanitizer _sanitizer;

    public LogisterExceptionMiddleware(
        RequestDelegate next,
        LogisterClient client,
        LogisterAspNetCoreOptions options,
        LogisterEventQueue queue,
        ILogger<LogisterExceptionMiddleware> logger)
    {
        _next = next;
        _client = client;
        _options = options;
        _queue = queue;
        _logger = logger;
        _sanitizer = new LogisterValueSanitizer(options.Client.RedactKeys);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        using var traceScope = new LogisterRequestScope(context.TraceIdentifier, context.Request.Headers["traceparent"].ToString());
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            CaptureException(context, exception);
            throw;
        }
    }

    // Builds the event on the request thread and hands it to the background queue; never awaits delivery.
    private void CaptureException(HttpContext context, Exception exception)
    {
        try
        {
            var prepared = _client.PrepareException(
                exception,
                new CaptureOptions
                {
                    Context = LogisterHttpContext.BuildContext(
                        context,
                        _options,
                        statusCode: ExceptionStatusCode(context),
                        sanitizer: _sanitizer),
                    RequestId = context.TraceIdentifier,
                    TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                    UserId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                });
            if (!_queue.TryEnqueue(prepared))
            {
                LogisterMiddlewareLog.Debug(_logger, null, "Logister event queue is full; dropped an ASP.NET Core exception.");
            }
        }
        catch (Exception logisterError)
        {
            LogisterMiddlewareLog.Debug(_logger, logisterError, "Failed to capture ASP.NET Core exception for Logister.");
        }
    }

    private static int ExceptionStatusCode(HttpContext context)
    {
        return context.Response.StatusCode >= StatusCodes.Status400BadRequest
            ? context.Response.StatusCode
            : StatusCodes.Status500InternalServerError;
    }
}

internal static class LogisterMiddlewareLog
{
    public static void Debug(ILogger logger, Exception? error, string message)
    {
        try
        {
            logger.LogDebug(error, "{Message}", message);
        }
        catch
        {
            // A failing log provider must not replace the application's own exception or response.
        }
    }
}
