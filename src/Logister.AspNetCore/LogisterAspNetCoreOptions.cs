namespace Logister.AspNetCore;

public sealed class LogisterAspNetCoreOptions
{
    private double _transactionSampleRate = 1.0;
    private int _queueCapacity = 1000;

    public LogisterOptions Client { get; } = LogisterOptions.FromEnvironment();
    public bool CaptureRequestTransactions { get; set; }
    public bool CaptureRequestSpans { get; set; }
    public bool CaptureRequestHeaders { get; set; }
    public bool CaptureRequestCookies { get; set; }

    /// <summary>Adds the connection's remote IP as <c>client_ip</c> and keeps forwarding headers. Off by default.</summary>
    public bool CaptureClientIp { get; set; }

    /// <summary>Fraction (0–1) of requests whose transaction and span are reported. Defaults to 1.</summary>
    public double TransactionSampleRate
    {
        get => _transactionSampleRate;
        set => _transactionSampleRate = value is >= 0 and <= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "TransactionSampleRate must be between 0 and 1.");
    }

    /// <summary>Maximum number of events waiting for background delivery. Further events are dropped.</summary>
    public int QueueCapacity
    {
        get => _queueCapacity;
        set => _queueCapacity = value > 0
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "QueueCapacity must be positive.");
    }

    /// <summary>How long the delivery service keeps sending queued events after shutdown begins.</summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public string RedactedCookieValue { get; set; } = LogisterValueSanitizer.Redacted;

    /// <summary>
    /// Cookies whose values may be captured when <see cref="CaptureRequestCookies"/> is enabled. Every other
    /// cookie value is redacted. Names that look like authentication, session or antiforgery cookies stay
    /// redacted even when listed here.
    /// </summary>
    public ISet<string> AllowedRequestCookieNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cookie names that are always redacted, including their ASP.NET Core chunks (<c>name</c>C1, C2…).</summary>
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

    /// <summary>Additional request header names whose values are redacted when headers are captured.</summary>
    public ISet<string> SensitiveRequestHeaderNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
