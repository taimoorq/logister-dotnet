namespace Logister;

public sealed class CheckInOptions
{
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public double? DurationMs { get; set; }
    public DateTimeOffset? CheckedAt { get; set; }
    public int? ExpectedIntervalSeconds { get; set; }
    public string? TraceId { get; set; }
    public string? RequestId { get; set; }
    public IDictionary<string, object?>? Context { get; set; }
}
