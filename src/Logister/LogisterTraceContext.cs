using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Logister;

/// <summary>An immutable snapshot which can be attached to a later error report.</summary>
public sealed record LogisterTraceContext(string TraceId, string SpanId, string? ParentSpanId, string? RequestId, ActivityTraceFlags Flags)
{
    public static LogisterTraceContext? Current
    {
        get
        {
            var activity = Activity.Current;
            if (activity?.IdFormat != ActivityIdFormat.W3C) return null;
            return new(activity.TraceId.ToHexString(), activity.SpanId.ToHexString(),
                activity.ParentSpanId == default ? null : activity.ParentSpanId.ToHexString(), LogisterRequestScope.RequestId, activity.ActivityTraceFlags);
        }
    }

    public LogisterTraceContext Child() => new(TraceId, ActivitySpanId.CreateRandom().ToHexString(), SpanId, RequestId, Flags);

    public IDictionary<string, object?> Fields() => new Dictionary<string, object?>
    {
        ["trace_id"] = TraceId, ["span_id"] = SpanId, ["parent_span_id"] = ParentSpanId, ["request_id"] = RequestId
    };

    /// <summary>For transports without native Activity propagation. Recheck each redirect hop or disable redirects.</summary>
    public IDictionary<string, string> HeadersFor(Uri destination, IEnumerable<Uri> allowedOrigins)
    {
        if (!Regex.IsMatch($"00-{TraceId}-{SpanId}-01", @"\A00-[0-9a-f]{32}-[0-9a-f]{16}-01\z") || TraceId == new string('0', 32) || SpanId == new string('0', 16) ||
            (RequestId is not null && !Regex.IsMatch(RequestId, @"\A[A-Za-z0-9._:-]{1,200}\z")) || !ValidOrigin(destination) || !allowedOrigins.Any(origin => ValidOrigin(origin) &&
            origin.Scheme == destination.Scheme && origin.IdnHost == destination.IdnHost && origin.Port == destination.Port))
            return new Dictionary<string, string>();
        var result = new Dictionary<string, string> { ["traceparent"] = $"00-{TraceId}-{SpanId}-{((int)Flags & 1):x2}" };
        if (RequestId is not null) result["x-request-id"] = RequestId;
        return result;
    }

    private static bool ValidOrigin(Uri uri) => uri.IsAbsoluteUri && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);
}
