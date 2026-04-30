using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Logister;

public sealed class LogisterClient : IDisposable
{
    private const string IngestPath = "/api/v1/ingest_events";
    private const string CheckInPath = "/api/v1/check_ins";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LogisterOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;

    public LogisterClient(LogisterOptions options)
        : this(options, new HttpClient(), disposeHttpClient: true)
    {
    }

    public LogisterClient(LogisterOptions options, HttpClient httpClient)
        : this(options, httpClient, disposeHttpClient: false)
    {
    }

    private LogisterClient(LogisterOptions options, HttpClient httpClient, bool disposeHttpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _disposeHttpClient = disposeHttpClient;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new ArgumentException("Logister requires an API key.", nameof(options));
        }

        if (_options.BaseUrl is null)
        {
            throw new ArgumentException("Logister requires a base URL.", nameof(options));
        }

        if (_options.Timeout > TimeSpan.Zero && _disposeHttpClient)
        {
            _httpClient.Timeout = _options.Timeout;
        }
    }

    public Task<LogisterResponse> CaptureExceptionAsync(
        Exception exception,
        CaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        options ??= new CaptureOptions();

        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        MergeContext(context, options.Context);
        context["exception"] = LogisterExceptionPayload.FromException(
            exception,
            includeData: _options.CaptureExceptionData);

        return SendEventAsync(
            eventType: "error",
            level: options.Level ?? "error",
            message: options.Message ?? FirstPresent(exception.Message, exception.GetType().Name) ?? "Exception",
            context: context,
            fingerprint: options.Fingerprint,
            occurredAt: options.OccurredAt,
            options: options,
            cancellationToken: cancellationToken);
    }

    public Task<LogisterResponse> CaptureMessageAsync(
        string message,
        CaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        options ??= new CaptureOptions();

        return SendEventAsync(
            eventType: "log",
            level: options.Level ?? "info",
            message: message,
            context: options.Context,
            fingerprint: options.Fingerprint,
            occurredAt: options.OccurredAt,
            options: options,
            cancellationToken: cancellationToken);
    }

    public Task<LogisterResponse> CaptureMetricAsync(
        string name,
        double value,
        MetricOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new MetricOptions();

        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        MergeContext(context, options.Context);
        context["metric"] = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["value"] = value,
            ["unit"] = options.Unit
        };
        context.TryAdd("value", value);
        if (!string.IsNullOrWhiteSpace(options.Unit))
        {
            context.TryAdd("unit", options.Unit);
        }

        return SendEventAsync(
            eventType: "metric",
            level: options.Level ?? "info",
            message: name,
            context: context,
            fingerprint: options.Fingerprint,
            occurredAt: options.OccurredAt,
            options: options,
            cancellationToken: cancellationToken);
    }

    public Task<LogisterResponse> CaptureTransactionAsync(
        string name,
        double durationMs,
        CaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new CaptureOptions();

        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        MergeContext(context, options.Context);
        context.TryAdd("transaction_name", name);
        context.TryAdd("duration_ms", durationMs);

        return SendEventAsync(
            eventType: "transaction",
            level: options.Level ?? "info",
            message: name,
            context: context,
            fingerprint: options.Fingerprint,
            occurredAt: options.OccurredAt,
            options: options,
            cancellationToken: cancellationToken);
    }

    public Task<LogisterResponse> SendEventAsync(
        string eventType,
        string level,
        string message,
        IDictionary<string, object?>? context = null,
        string? fingerprint = null,
        DateTimeOffset? occurredAt = null,
        CaptureOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var eventContext = BuildContext(
            context,
            environment: options?.Environment,
            release: options?.Release,
            traceId: options?.TraceId,
            requestId: options?.RequestId,
            sessionId: options?.SessionId,
            userId: options?.UserId);

        var payload = new IngestEnvelope(new IngestEventPayload(
            EventType: eventType,
            Level: level,
            Message: message,
            Fingerprint: fingerprint,
            OccurredAt: NormalizeTimestamp(occurredAt),
            Context: eventContext));

        return PostJsonAsync(IngestPath, payload, cancellationToken);
    }

    public Task<LogisterResponse> CheckInAsync(
        string slug,
        string status = "ok",
        CheckInOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        options ??= new CheckInOptions();

        var context = BuildContext(
            options.Context,
            environment: options.Environment,
            release: options.Release,
            traceId: options.TraceId,
            requestId: options.RequestId);

        var payload = new CheckInEnvelope(new CheckInPayload(
            Slug: slug,
            Status: status,
            Environment: FirstPresent(options.Environment, _options.Environment),
            DurationMs: options.DurationMs,
            CheckedAt: NormalizeTimestamp(options.CheckedAt),
            ExpectedIntervalSeconds: options.ExpectedIntervalSeconds,
            TraceId: options.TraceId,
            RequestId: options.RequestId,
            Context: context.Count == 0 ? null : context));

        return PostJsonAsync(CheckInPath, payload, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<LogisterResponse> PostJsonAsync(
        string path,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new LogisterException(
                $"Logister request failed with status {(int)response.StatusCode}: {responseBody}",
                response.StatusCode,
                responseBody);
        }

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return new LogisterResponse(null, null, "accepted");
        }

        try
        {
            return JsonSerializer.Deserialize<LogisterResponse>(responseBody, JsonOptions) ??
                new LogisterResponse(null, null, "accepted");
        }
        catch (JsonException)
        {
            return new LogisterResponse(null, null, "accepted");
        }
    }

    private Dictionary<string, object?> BuildContext(
        IDictionary<string, object?>? context,
        string? environment = null,
        string? release = null,
        string? traceId = null,
        string? requestId = null,
        string? sessionId = null,
        string? userId = null)
    {
        var merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        MergeContext(merged, _options.DefaultContext);
        MergeContext(merged, context);

        SetIfMissing(merged, "environment", FirstPresent(environment, _options.Environment));
        SetIfMissing(merged, "release", FirstPresent(release, _options.Release));
        SetIfMissing(merged, "trace_id", traceId);
        SetIfMissing(merged, "request_id", requestId);
        SetIfMissing(merged, "session_id", sessionId);
        SetIfMissing(merged, "user_id", userId);
        SetIfMissing(merged, "runtime", ".NET");
        SetIfMissing(merged, "dotnet_version", System.Environment.Version.ToString());
        SetIfMissing(merged, "framework_description", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        SetIfMissing(merged, "os_description", System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        SetIfMissing(merged, "process_id", System.Environment.ProcessId);
        SetIfMissing(merged, "machine_name", System.Environment.MachineName);

        return merged;
    }

    private static void MergeContext(
        IDictionary<string, object?> target,
        IDictionary<string, object?>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            target[key] = LogisterValueNormalizer.Normalize(value);
        }
    }

    private static void SetIfMissing(IDictionary<string, object?> target, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        if (value is string stringValue && string.IsNullOrWhiteSpace(stringValue))
        {
            return;
        }

        if (!target.ContainsKey(key))
        {
            target[key] = LogisterValueNormalizer.Normalize(value);
        }
    }

    private static string NormalizeTimestamp(DateTimeOffset? timestamp)
    {
        return (timestamp ?? DateTimeOffset.UtcNow).ToUniversalTime().ToString("O");
    }

    private static string? FirstPresent(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record IngestEnvelope([property: JsonPropertyName("event")] IngestEventPayload Event);

    private sealed record IngestEventPayload(
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("fingerprint")] string? Fingerprint,
        [property: JsonPropertyName("occurred_at")] string OccurredAt,
        [property: JsonPropertyName("context")] IDictionary<string, object?> Context);

    private sealed record CheckInEnvelope([property: JsonPropertyName("check_in")] CheckInPayload CheckIn);

    private sealed record CheckInPayload(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("environment")] string? Environment,
        [property: JsonPropertyName("duration_ms")] double? DurationMs,
        [property: JsonPropertyName("checked_at")] string CheckedAt,
        [property: JsonPropertyName("expected_interval_seconds")] int? ExpectedIntervalSeconds,
        [property: JsonPropertyName("trace_id")] string? TraceId,
        [property: JsonPropertyName("request_id")] string? RequestId,
        [property: JsonPropertyName("context")] IDictionary<string, object?>? Context);
}
