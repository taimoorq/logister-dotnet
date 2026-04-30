namespace Logister.AspNetCore;

public sealed class LogisterAspNetCoreOptions
{
    public LogisterOptions Client { get; } = LogisterOptions.FromEnvironment();
    public bool CaptureRequestTransactions { get; set; }
    public bool CaptureRequestHeaders { get; set; }
}
