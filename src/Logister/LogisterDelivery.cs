using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Logister;

public sealed class PreparedEvent
{
    internal PreparedEvent(Guid eventId, DateTimeOffset occurredAt, string envelope)
    {
        EventId = eventId;
        OccurredAt = occurredAt;
        Envelope = envelope;
    }

    public Guid EventId { get; }
    public DateTimeOffset OccurredAt { get; }
    internal string Envelope { get; }
}

public sealed record DeliveryResult(Guid EventId, LogisterResponse? Response = null, Exception? Error = null);

public sealed class RetryPolicy
{
    public RetryPolicy(int maximumAttempts = 3, TimeSpan? baseDelay = null, TimeSpan? maximumDelay = null, TimeSpan? totalTimeout = null)
    {
        MaximumAttempts = maximumAttempts;
        BaseDelay = baseDelay ?? TimeSpan.FromMilliseconds(250);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(5);
        TotalTimeout = totalTimeout ?? TimeSpan.FromSeconds(15);
        if (maximumAttempts is < 1 or > 10 || BaseDelay < TimeSpan.Zero || MaximumDelay < TimeSpan.Zero || TotalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts), "Use 1–10 attempts, nonnegative delays and a positive timeout.");
    }

    public int MaximumAttempts { get; }
    public TimeSpan BaseDelay { get; }
    public TimeSpan MaximumDelay { get; }
    public TimeSpan TotalTimeout { get; }
}

public sealed partial class LogisterClient
{
    public PreparedEvent PrepareEvent(
        string eventType, string level, string message,
        IDictionary<string, object?>? context = null, string? fingerprint = null,
        DateTimeOffset? occurredAt = null, CaptureOptions? options = null, Guid? eventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var id = eventId ?? Guid.NewGuid();
        var capturedAt = occurredAt ?? options?.OccurredAt ?? DateTimeOffset.UtcNow;
        var payload = new IngestEnvelope(new IngestEventPayload(
            EventType: eventType, Level: level, Message: message, Fingerprint: fingerprint ?? options?.Fingerprint,
            OccurredAt: NormalizeTimestamp(capturedAt),
            Context: BuildContext(context ?? options?.Context, options?.Environment, options?.Release, options?.TraceId,
                options?.RequestId, options?.SessionId, options?.UserId),
            Uuid: id));
        return new PreparedEvent(id, capturedAt, JsonSerializer.Serialize(payload, JsonOptions));
    }

    public Task<LogisterResponse> SendPreparedEventAsync(PreparedEvent prepared, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        return PostBytesAsync(IngestPath, Encoding.UTF8.GetBytes(prepared.Envelope), "application/json", cancellationToken);
    }

    public async Task<IReadOnlyList<DeliveryResult>> SendEventsAsync(IEnumerable<PreparedEvent> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        var prepared = new List<PreparedEvent>();
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(item);
            if (prepared.Count == 1000) throw new ArgumentException("At most 1,000 events may be sent in one call.", nameof(events));
            prepared.Add(item);
        }
        var results = new List<DeliveryResult>(prepared.Count);
        var batchStarted = Stopwatch.GetTimestamp();

        async Task SendChunk(PreparedEvent[] chunk)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = Encoding.UTF8.GetBytes(string.Join("\n", chunk.Select(item => item.Envelope)) + "\n");
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(raw);
            var body = compressed.ToArray();
            if (chunk.Length > 1 && (raw.Length > 8 * 1024 * 1024 || body.Length > 2 * 1024 * 1024))
            {
                await Split(chunk).ConfigureAwait(false);
                return;
            }
            try
            {
                var response = await PostBytesAsync(IngestPath + "/batch", body, "application/x-ndjson", cancellationToken,
                    Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant(), batchStarted).ConfigureAwait(false);
                results.AddRange(chunk.Select(item => new DeliveryResult(item.EventId, Response: response)));
            }
            catch (Exception error) when (IsDeliveryFailure(error) && !cancellationToken.IsCancellationRequested)
            {
                var status = error is LogisterException rejected ? (int)rejected.StatusCode : 0;
                if (status == 413 && chunk.Length > 1)
                    await Split(chunk).ConfigureAwait(false);
                else if (status is 404 or 405 or 415 or 501 || status == 413 && chunk.Length == 1)
                {
                    foreach (var item in chunk)
                    {
                        try
                        {
                            var response = await PostBytesAsync(IngestPath, Encoding.UTF8.GetBytes(item.Envelope), "application/json", cancellationToken,
                                startedTimestamp: batchStarted).ConfigureAwait(false);
                            results.Add(new DeliveryResult(item.EventId, Response: response));
                        }
                        catch (Exception failure) when (IsDeliveryFailure(failure) && !cancellationToken.IsCancellationRequested)
                        {
                            results.Add(new DeliveryResult(item.EventId, Error: failure));
                        }
                    }
                }
                else results.AddRange(chunk.Select(item => new DeliveryResult(item.EventId, Error: error)));
            }
        }

        async Task Split(PreparedEvent[] chunk)
        {
            var middle = chunk.Length / 2;
            await SendChunk(chunk[..middle]).ConfigureAwait(false);
            await SendChunk(chunk[middle..]).ConfigureAwait(false);
        }

        foreach (var chunk in prepared.Chunk(100)) await SendChunk(chunk).ConfigureAwait(false);
        return results;
    }

    private static bool IsDeliveryFailure(Exception exception) =>
        exception is LogisterException or HttpRequestException or OperationCanceledException or TimeoutException;

    private async Task<LogisterResponse> PostBytesAsync(string path, byte[] body, string mediaType,
        CancellationToken cancellationToken, string? batchId = null, long? startedTimestamp = null)
    {
        var retry = path == IngestPath || path == IngestPath + "/batch";
        var policy = _options.RetryPolicy;
        var started = startedTimestamp ?? Stopwatch.GetTimestamp();
        var uri = new Uri(_options.BaseUrl, path);
        var apiKey = _options.ApiKey;
        var userAgent = _options.UserAgent;
        var attempts = retry ? policy.MaximumAttempts : 1;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = policy.TotalTimeout - Stopwatch.GetElapsedTime(started);
            if (retry && remaining <= TimeSpan.Zero) throw new TimeoutException("Ingestion retry deadline exhausted.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (retry) deadline.CancelAfter(remaining);
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            if (batchId is not null)
            {
                request.Headers.Add("X-Logister-Batch-Id", batchId);
                request.Content.Headers.ContentEncoding.Add("gzip");
            }
            Exception failure;
            var retryable = true;
            var delaySeconds = Math.Min(policy.BaseDelay.TotalSeconds * Math.Pow(2, attempt), policy.MaximumDelay.TotalSeconds);
            try
            {
                using var response = await _httpClient.SendAsync(request, deadline.Token).ConfigureAwait(false);
                var responseBody = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ParseResponse(responseBody);
                var status = (int)response.StatusCode;
                failure = new LogisterException($"Logister request failed with status {status}: {responseBody}", response.StatusCode, responseBody);
                retryable = status is 408 or 425 or 429 || status >= 500 && status <= 599;
                if (batchId is not null && status == 501) retryable = false;
                try
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var seconds = retryAfter?.Delta?.TotalSeconds ?? (retryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds;
                    if (seconds >= 0) delaySeconds = Math.Min(seconds.Value, policy.MaximumDelay.TotalSeconds);
                }
                catch (FormatException) { /* Invalid Retry-After uses capped backoff. */ }
            }
            catch (HttpRequestException error) { failure = error; }
            catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested) { failure = error; }
            if (!retryable || attempt + 1 == attempts) ExceptionDispatchInfo.Capture(failure).Throw();
            var delay = TimeSpan.FromSeconds(delaySeconds);
            if (delay >= policy.TotalTimeout - Stopwatch.GetElapsedTime(started))
                throw new TimeoutException("Ingestion retry deadline exhausted.", failure);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Unreachable delivery state.");
    }

    private static LogisterResponse ParseResponse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new LogisterResponse(null, null, "accepted");
        try { return JsonSerializer.Deserialize<LogisterResponse>(body, JsonOptions) ?? new LogisterResponse(null, null, "accepted"); }
        catch (JsonException) { return new LogisterResponse(null, null, "accepted"); }
    }
}
