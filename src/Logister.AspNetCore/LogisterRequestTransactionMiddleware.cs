using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Logister.AspNetCore;

public sealed class LogisterRequestTransactionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LogisterClient _client;
    private readonly LogisterAspNetCoreOptions _options;
    private readonly LogisterEventQueue _queue;
    private readonly ILogger<LogisterRequestTransactionMiddleware> _logger;
    private readonly LogisterValueSanitizer _sanitizer;

    public LogisterRequestTransactionMiddleware(
        RequestDelegate next,
        LogisterClient client,
        LogisterAspNetCoreOptions options,
        LogisterEventQueue queue,
        ILogger<LogisterRequestTransactionMiddleware> logger)
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
        if ((!_options.CaptureRequestTransactions && !_options.CaptureRequestSpans) || !Sampled())
        {
            await _next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            stopwatch.Stop();
            if (_options.CaptureRequestTransactions)
            {
                CaptureTransaction(context, stopwatch.Elapsed.TotalMilliseconds, failed);
            }

            if (_options.CaptureRequestSpans)
            {
                CaptureSpan(context, stopwatch.Elapsed.TotalMilliseconds, failed);
            }
        }
    }

    private bool Sampled()
    {
        var rate = _options.TransactionSampleRate;
        return rate >= 1 || (rate > 0 && Random.Shared.NextDouble() < rate);
    }

    private void CaptureTransaction(HttpContext context, double durationMs, bool failed)
    {
        try
        {
            Enqueue(_client.PrepareTransaction(
                LogisterHttpContext.TransactionName(context, _sanitizer),
                durationMs,
                new CaptureOptions
                {
                    Level = (failed || context.Response.StatusCode >= 500) ? "error" : "info",
                    Context = LogisterHttpContext.BuildContext(context, _options, durationMs, failed ? 500 : null, _sanitizer),
                    RequestId = context.TraceIdentifier,
                    TraceId = Activity.Current?.TraceId.ToString(),
                    UserId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                }), "transaction");
        }
        catch (Exception logisterError)
        {
            LogisterMiddlewareLog.Debug(_logger, logisterError, "Failed to capture ASP.NET Core transaction for Logister.");
        }
    }

    private void CaptureSpan(HttpContext context, double durationMs, bool failed)
    {
        try
        {
            var activity = Activity.Current;
            Enqueue(_client.PrepareSpan(
                LogisterHttpContext.TransactionName(context, _sanitizer),
                durationMs,
                new SpanOptions
                {
                    Kind = "server",
                    Status = (failed || context.Response.StatusCode >= 500) ? "error" : "ok",
                    Context = LogisterHttpContext.BuildContext(context, _options, durationMs, failed ? 500 : null, _sanitizer),
                    RequestId = context.TraceIdentifier,
                    TraceId = activity?.TraceId.ToString() ?? context.TraceIdentifier,
                    SpanId = activity?.SpanId.ToString(),
                    ParentSpanId = LogisterTraceContext.Current?.ParentSpanId,
                    UserId = context.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                }), "request span");
        }
        catch (Exception logisterError)
        {
            LogisterMiddlewareLog.Debug(_logger, logisterError, "Failed to capture ASP.NET Core request span for Logister.");
        }
    }

    private void Enqueue(PreparedEvent prepared, string kind)
    {
        if (!_queue.TryEnqueue(prepared))
        {
            LogisterMiddlewareLog.Debug(_logger, null, $"Logister event queue is full; dropped an ASP.NET Core {kind}.");
        }
    }
}
