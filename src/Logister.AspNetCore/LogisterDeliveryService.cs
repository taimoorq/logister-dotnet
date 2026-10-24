using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Logister.AspNetCore;

/// <summary>Drains <see cref="LogisterEventQueue"/> in batches through <see cref="LogisterClient.SendEventsAsync"/>.</summary>
internal sealed class LogisterDeliveryService : BackgroundService
{
    internal const int MaxBatchSize = 100;

    private readonly LogisterEventQueue _queue;
    private readonly IServiceScopeFactory _scopes;
    private readonly LogisterAspNetCoreOptions _options;
    private readonly ILogger<LogisterDeliveryService> _logger;

    public LogisterDeliveryService(
        LogisterEventQueue queue,
        IServiceScopeFactory scopes,
        LogisterAspNetCoreOptions options,
        ILogger<LogisterDeliveryService> logger)
    {
        _queue = queue;
        _scopes = scopes;
        _options = options;
        _logger = logger;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Resolve once so missing or invalid configuration fails at startup with LogisterConfigurationException.
        using (var scope = _scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<LogisterClient>();
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sends are not cancelled the moment shutdown starts: the in-flight batch and any queued events
        // get up to ShutdownFlushTimeout to finish.
        using var sendCancellation = new CancellationTokenSource();
        using var shutdown = stoppingToken.Register(() =>
        {
            try
            {
                if (_options.ShutdownFlushTimeout > TimeSpan.Zero) sendCancellation.CancelAfter(_options.ShutdownFlushTimeout);
                else sendCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        });

        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await SendBatchAsync(sendCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || sendCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Log(error, "Logister background delivery stopped unexpectedly.");
        }

        try
        {
            while (_queue.Count > 0 && !sendCancellation.IsCancellationRequested)
            {
                await SendBatchAsync(sendCancellation.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Log(error, "Failed to flush queued Logister events during shutdown.");
        }
    }

    internal async Task SendBatchAsync(CancellationToken cancellationToken)
    {
        var batch = new List<PreparedEvent>(MaxBatchSize);
        while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var prepared))
        {
            batch.Add(prepared);
        }

        if (batch.Count == 0) return;
        try
        {
            using var scope = _scopes.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<LogisterClient>();
            var results = await client.SendEventsAsync(batch, cancellationToken).ConfigureAwait(false);
            var failed = results.FirstOrDefault(result => result.Error is not null);
            if (failed is not null)
            {
                Log(failed.Error!, $"Logister rejected or failed {results.Count(result => result.Error is not null)} of {batch.Count} queued events.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            Log(error, $"Failed to send {batch.Count} queued Logister events.");
        }
    }

    private void Log(Exception error, string message)
    {
        try
        {
            _logger.LogDebug(error, "{Message}", message);
        }
        catch
        {
            // Logging must never take down the host.
        }
    }
}
