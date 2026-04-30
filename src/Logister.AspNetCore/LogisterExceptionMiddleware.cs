using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Logister.AspNetCore;

public sealed class LogisterExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LogisterClient _client;
    private readonly LogisterAspNetCoreOptions _options;
    private readonly ILogger<LogisterExceptionMiddleware> _logger;

    public LogisterExceptionMiddleware(
        RequestDelegate next,
        LogisterClient client,
        LogisterAspNetCoreOptions options,
        ILogger<LogisterExceptionMiddleware> logger)
    {
        _next = next;
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception exception)
        {
            await CaptureExceptionAsync(context, exception);
            throw;
        }
    }

    private async Task CaptureExceptionAsync(HttpContext context, Exception exception)
    {
        try
        {
            await _client.CaptureExceptionAsync(
                exception,
                new CaptureOptions
                {
                    Context = LogisterHttpContext.BuildContext(context, _options),
                    RequestId = context.TraceIdentifier,
                    TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                    UserId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                },
                context.RequestAborted);
        }
        catch (Exception logisterError)
        {
            _logger.LogDebug(logisterError, "Failed to send ASP.NET Core exception to Logister.");
        }
    }
}
