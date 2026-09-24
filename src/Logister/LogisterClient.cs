using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Logister;

public sealed partial class LogisterClient : IDisposable
{
    private const string IngestPath = "/api/v1/ingest_events";
    private const string CheckInPath = "/api/v1/check_ins";
    private const string DeploymentPath = "/api/v1/deployments";

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

    public Task<LogisterResponse> CaptureSpanAsync(
        string name,
        double durationMs,
        SpanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        options ??= new SpanOptions();

        var spanId = FirstPresent(options.SpanId, NewSpanId())!;
        var active = LogisterTraceContext.Current;
        var traceId = FirstPresent(options.TraceId, active?.TraceId, ActivityTraceId.CreateRandom().ToHexString());
        var requestId = FirstPresent(options.RequestId, active?.RequestId);
        var parentSpanId = FirstPresent(options.ParentSpanId, spanId == active?.SpanId ? active.ParentSpanId : active?.SpanId);
        var kind = FirstPresent(options.Kind, "internal")!;
        var startedAt = options.StartedAt ?? options.OccurredAt ?? DateTimeOffset.UtcNow;

        var context = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        MergeContext(context, options.Context);
        SetIfMissing(context, "name", name);
        SetIfMissing(context, "trace_id", traceId);
        SetIfMissing(context, "request_id", requestId);
        SetIfMissing(context, "span_id", spanId);
        SetIfMissing(context, "parent_span_id", parentSpanId);
        SetIfMissing(context, "span_kind", kind);
        SetIfMissing(context, "kind", kind);
        SetIfMissing(context, "status", options.Status);
        SetIfMissing(context, "duration_ms", durationMs);
        SetIfMissing(context, "started_at", NormalizeTimestamp(startedAt));
        SetIfMissing(context, "ended_at", NormalizeOptionalTimestamp(options.EndedAt));

        var eventContext = BuildContext(
            context,
            environment: options.Environment,
            release: options.Release,
            traceId: traceId,
            requestId: requestId,
            sessionId: options.SessionId,
            userId: options.UserId);

        var payload = new IngestEnvelope(new IngestEventPayload(
            EventType: "span",
            Level: options.Level ?? (string.Equals(options.Status, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "info"),
            Message: options.Message ?? name,
            Fingerprint: options.Fingerprint,
            OccurredAt: NormalizeTimestamp(options.OccurredAt ?? startedAt),
            Context: eventContext,
            Name: name,
            DurationMs: durationMs,
            TraceId: traceId,
            RequestId: requestId,
            SpanId: spanId,
            ParentSpanId: parentSpanId,
            Kind: kind,
            Status: options.Status,
            StartedAt: NormalizeTimestamp(startedAt),
            EndedAt: NormalizeOptionalTimestamp(options.EndedAt)));

        return PostJsonAsync(IngestPath, payload, cancellationToken);
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
        return SendPreparedEventAsync(PrepareEvent(eventType, level, message, context, fingerprint, occurredAt, options), cancellationToken);
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
            Release: FirstPresent(options.Release, _options.Release),
            DurationMs: options.DurationMs,
            OccurredAt: NormalizeTimestamp(options.CheckedAt),
            ExpectedIntervalSeconds: options.ExpectedIntervalSeconds,
            TraceId: options.TraceId,
            RequestId: options.RequestId,
            Context: context.Count == 0 ? null : context));

        return PostJsonAsync(CheckInPath, payload, cancellationToken);
    }

    public Task<LogisterResponse> RecordDeploymentAsync(
        DeploymentOptions deployment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);

        var release = FirstPresent(deployment.Release, _options.Release);
        var repository = FirstPresent(deployment.Repository, _options.Repository);
        var commitSha = FirstPresent(deployment.CommitSha, _options.CommitSha);

        ArgumentException.ThrowIfNullOrWhiteSpace(release);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitSha);

        var payload = new DeploymentEnvelope(new DeploymentPayload(
            Release: release,
            Environment: FirstPresent(deployment.Environment, _options.Environment),
            Repository: repository,
            CommitSha: commitSha,
            Branch: FirstPresent(deployment.Branch, _options.Branch),
            DeployedAt: NormalizeOptionalTimestamp(deployment.DeployedAt),
            PullRequestNumber: deployment.PullRequestNumber,
            PullRequestUrl: deployment.PullRequestUrl,
            ReleaseTag: deployment.ReleaseTag,
            ReleaseUrl: deployment.ReleaseUrl,
            CompareUrl: deployment.CompareUrl,
            WorkflowRunUrl: deployment.WorkflowRunUrl,
            DeploymentUrl: deployment.DeploymentUrl));

        return PostJsonAsync(DeploymentPath, payload, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private Task<LogisterResponse> PostJsonAsync(string path, object payload, CancellationToken cancellationToken)
    {
        if (payload is IngestEnvelope envelope)
            payload = envelope with { Event = envelope.Event with { Uuid = envelope.Event.Uuid ?? Guid.NewGuid() } };
        return PostBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), "application/json", cancellationToken);
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
        SetIfMissing(merged, "repository", _options.Repository);
        SetIfMissing(merged, "commit_sha", _options.CommitSha);
        SetIfMissing(merged, "branch", _options.Branch);
        SetIfMissing(merged, "trace_id", traceId);
        SetIfMissing(merged, "request_id", requestId);
        SetIfMissing(merged, "session_id", sessionId);
        SetIfMissing(merged, "user_id", userId);
        var active = LogisterTraceContext.Current;
        if (active is not null)
            foreach (var field in active.Fields()) SetIfMissing(merged, field.Key, field.Value);
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

    private static string? NormalizeOptionalTimestamp(DateTimeOffset? timestamp)
    {
        return timestamp?.ToUniversalTime().ToString("O");
    }

    private static string? FirstPresent(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string NewSpanId()
    {
        return Guid.NewGuid().ToString("N")[..16];
    }

    private sealed record IngestEnvelope([property: JsonPropertyName("event")] IngestEventPayload Event);

    private sealed record IngestEventPayload(
        [property: JsonPropertyName("event_type")] string EventType,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("fingerprint")] string? Fingerprint,
        [property: JsonPropertyName("occurred_at")] string OccurredAt,
        [property: JsonPropertyName("context")] IDictionary<string, object?> Context,
        [property: JsonPropertyName("name")] string? Name = null,
        [property: JsonPropertyName("duration_ms")] double? DurationMs = null,
        [property: JsonPropertyName("trace_id")] string? TraceId = null,
        [property: JsonPropertyName("request_id")] string? RequestId = null,
        [property: JsonPropertyName("span_id")] string? SpanId = null,
        [property: JsonPropertyName("parent_span_id")] string? ParentSpanId = null,
        [property: JsonPropertyName("kind")] string? Kind = null,
        [property: JsonPropertyName("status")] string? Status = null,
        [property: JsonPropertyName("started_at")] string? StartedAt = null,
        [property: JsonPropertyName("ended_at")] string? EndedAt = null,
        [property: JsonPropertyName("uuid")] Guid? Uuid = null);

    private sealed record CheckInEnvelope([property: JsonPropertyName("check_in")] CheckInPayload CheckIn);

    private sealed record CheckInPayload(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("environment")] string? Environment,
        [property: JsonPropertyName("release")] string? Release,
        [property: JsonPropertyName("duration_ms")] double? DurationMs,
        [property: JsonPropertyName("occurred_at")] string OccurredAt,
        [property: JsonPropertyName("expected_interval_seconds")] int? ExpectedIntervalSeconds,
        [property: JsonPropertyName("trace_id")] string? TraceId,
        [property: JsonPropertyName("request_id")] string? RequestId,
        [property: JsonPropertyName("context")] IDictionary<string, object?>? Context);

    private sealed record DeploymentEnvelope([property: JsonPropertyName("deployment")] DeploymentPayload Deployment);

    private sealed record DeploymentPayload(
        [property: JsonPropertyName("release")] string Release,
        [property: JsonPropertyName("environment")] string? Environment,
        [property: JsonPropertyName("repository")] string Repository,
        [property: JsonPropertyName("commit_sha")] string CommitSha,
        [property: JsonPropertyName("branch")] string? Branch,
        [property: JsonPropertyName("deployed_at")] string? DeployedAt,
        [property: JsonPropertyName("pull_request_number")] int? PullRequestNumber,
        [property: JsonPropertyName("pull_request_url")] string? PullRequestUrl,
        [property: JsonPropertyName("release_tag")] string? ReleaseTag,
        [property: JsonPropertyName("release_url")] string? ReleaseUrl,
        [property: JsonPropertyName("compare_url")] string? CompareUrl,
        [property: JsonPropertyName("workflow_run_url")] string? WorkflowRunUrl,
        [property: JsonPropertyName("deployment_url")] string? DeploymentUrl);
}
