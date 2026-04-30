using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Logister.AspNetCore;

public sealed class LogisterRequestTransactionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LogisterClient _client;
    private readonly LogisterAspNetCoreOptions _options;
    private readonly ILogger<LogisterRequestTransactionMiddleware> _logger;

    public LogisterRequestTransactionMiddleware(
        RequestDelegate next,
        LogisterClient client,
        LogisterAspNetCoreOptions options,
        ILogger<LogisterRequestTransactionMiddleware> logger)
    {
        _next = next;
        _client = client;
        _options = options;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_options.CaptureRequestTransactions)
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            await _next(context);
        }
        finally
        {
            stopwatch.Stop();
            await CaptureTransactionAsync(context, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task CaptureTransactionAsync(HttpContext context, double durationMs)
    {
        try
        {
            var transactionName = context.GetEndpoint()?.DisplayName ?? $"{context.Request.Method} {context.Request.Path}";
            await _client.CaptureTransactionAsync(
                transactionName,
                durationMs,
                new CaptureOptions
                {
                    Level = context.Response.StatusCode >= 500 ? "error" : "info",
                    Context = LogisterHttpContext.BuildContext(context, _options, durationMs),
                    RequestId = context.TraceIdentifier,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    UserId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                },
                context.RequestAborted);
        }
        catch (Exception logisterError)
        {
            _logger.LogDebug(logisterError, "Failed to send ASP.NET Core transaction to Logister.");
        }
    }
}
