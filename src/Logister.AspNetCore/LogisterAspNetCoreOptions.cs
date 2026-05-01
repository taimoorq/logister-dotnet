namespace Logister.AspNetCore;

public sealed class LogisterAspNetCoreOptions
{
    public LogisterOptions Client { get; } = LogisterOptions.FromEnvironment();
    public bool CaptureRequestTransactions { get; set; }
    public bool CaptureRequestHeaders { get; set; }
    public bool CaptureRequestCookies { get; set; }
    public string RedactedCookieValue { get; set; } = "[Filtered]";
    public ISet<string> SensitiveRequestCookieNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".AspNetCore.Cookies",
        ".AspNetCore.Identity.Application",
        ".AspNetCore.Session",
        "access_token",
        "auth",
        "authentication",
        "authorization",
        "id_token",
        "jwt",
        "refresh_token",
        "session",
        "sessionid",
        "sid",
        "token"
    };
}
