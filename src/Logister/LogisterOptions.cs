namespace Logister;

public sealed class LogisterOptions
{
    public string? ApiKey { get; set; }

    /// <summary>Your Logister instance, for example <c>https://logister.example.com</c>. There is no default.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>Allows a non-loopback <c>http://</c> base URL. Loopback hosts are always allowed.</summary>
    public bool AllowInsecureHttp { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public string? Repository { get; set; }
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public IDictionary<string, object?> DefaultContext { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public bool CaptureExceptionData { get; set; } = true;

    /// <summary>Adds <c>machine_name</c> and <c>process_id</c> to every event. Off by default.</summary>
    public bool CaptureHostIdentity { get; set; }

    /// <summary>
    /// Extra context keys to redact in addition to the built-in list. Entries are normalized like the built-in
    /// list (lowercased, non-alphanumerics removed) and must match exactly, so <c>"tenant-key"</c> matches
    /// <c>tenant_key</c> and <c>TenantKey</c>. Configure these before creating the client.
    /// </summary>
    public ISet<string> RedactKeys { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public RetryPolicy RetryPolicy { get; set; } = new();
    public string UserAgent { get; set; } = "logister-dotnet/0.5.0";

    public static LogisterOptions FromEnvironment()
    {
        var options = new LogisterOptions
        {
            ApiKey = ReadEnv("LOGISTER_API_KEY"),
            Environment = ReadEnv("LOGISTER_ENVIRONMENT"),
            Release = ReadEnv("LOGISTER_RELEASE"),
            Repository = ReadEnv("LOGISTER_REPOSITORY") ?? ReadEnv("GITHUB_REPOSITORY"),
            CommitSha = ReadEnv("LOGISTER_COMMIT_SHA") ?? ReadEnv("GITHUB_SHA"),
            Branch = ReadEnv("LOGISTER_BRANCH") ?? ReadEnv("GITHUB_REF_NAME")
        };

        var baseUrl = ReadEnv("LOGISTER_BASE_URL");
        if (baseUrl is not null)
        {
            options.BaseUrl = ParseBaseUrl(baseUrl, "LOGISTER_BASE_URL");
        }

        if (bool.TryParse(ReadEnv("LOGISTER_ALLOW_INSECURE_HTTP"), out var allowInsecureHttp))
        {
            options.AllowInsecureHttp = allowInsecureHttp;
        }

        var timeout = ReadEnv("LOGISTER_TIMEOUT");
        if (double.TryParse(timeout, out var timeoutSeconds) && timeoutSeconds > 0)
        {
            options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        foreach (var key in (ReadEnv("LOGISTER_REDACT_KEYS") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.RedactKeys.Add(key);
        }

        return options;
    }

    /// <summary>Parses a configured base URL, throwing <see cref="LogisterConfigurationException"/> when it is not absolute.</summary>
    public static Uri ParseBaseUrl(string value, string source = "BaseUrl")
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri))
        {
            throw new LogisterConfigurationException(
                $"{source} must be an absolute URL to your Logister instance, for example https://logister.example.com.");
        }

        return uri;
    }

    internal Uri ValidatedBaseUrl()
    {
        var uri = BaseUrl ?? throw new LogisterConfigurationException(
            "Logister requires a base URL. Set LogisterOptions.BaseUrl, the Logister:BaseUrl setting, or LOGISTER_BASE_URL " +
            "to your Logister instance, for example https://logister.example.com.");

        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http"))
        {
            throw new LogisterConfigurationException("Logister base URL must be an absolute https:// URL.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new LogisterConfigurationException("Logister base URL must not contain credentials; use ApiKey instead.");
        }

        if (uri.Scheme == "http" && !uri.IsLoopback && !AllowInsecureHttp)
        {
            throw new LogisterConfigurationException(
                $"Logister base URL {uri.GetLeftPart(UriPartial.Authority)} uses http://, which would send the API key in clear text. " +
                "Use https://, a loopback host, or set AllowInsecureHttp = true.");
        }

        return uri;
    }

    private static string? ReadEnv(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
