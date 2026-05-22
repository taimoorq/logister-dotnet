namespace Logister;

public sealed class SpanOptions : CaptureOptions
{
    public string? SpanId { get; set; }
    public string? ParentSpanId { get; set; }
    public string? Kind { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
}
