using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Logister;

/// <summary>Reuses the host's W3C Activity and restores task-local state on dispose.</summary>
public sealed class LogisterRequestScope : IDisposable
{
    private static readonly AsyncLocal<string?> Request = new();
    internal static string? RequestId => Request.Value;
    private readonly string? _previous;
    private readonly Activity? _ownedActivity;

    public LogisterRequestScope(string? requestId, string? traceparent = null)
    {
        _previous = Request.Value;
        Request.Value = requestId is not null && Regex.IsMatch(requestId, "\\A[A-Za-z0-9._:-]{1,200}\\z") ? requestId : Guid.NewGuid().ToString();
        if (Activity.Current?.IdFormat != ActivityIdFormat.W3C)
        {
            _ownedActivity = new Activity("Logister request").SetIdFormat(ActivityIdFormat.W3C);
            if (traceparent is not null && Regex.IsMatch(traceparent, "\\A00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}\\z") &&
                ActivityContext.TryParse(traceparent, null, out var parent) && parent.TraceId != default && parent.SpanId != default)
                _ownedActivity.SetParentId(parent.TraceId, parent.SpanId, parent.TraceFlags);
            _ownedActivity.Start();
        }
    }

    public void Dispose()
    {
        _ownedActivity?.Dispose();
        Request.Value = _previous;
    }
}
