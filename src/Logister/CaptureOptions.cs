namespace Logister;

public class CaptureOptions
{
    public string? Level { get; set; }
    public string? Message { get; set; }
    public string? Fingerprint { get; set; }
    public DateTimeOffset? OccurredAt { get; set; }
    public IDictionary<string, object?>? Context { get; set; }
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public string? TraceId { get; set; }
    public string? RequestId { get; set; }
    public string? SessionId { get; set; }
    public string? UserId { get; set; }
}
