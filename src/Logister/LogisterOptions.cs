namespace Logister;

public sealed class LogisterOptions
{
    public string? ApiKey { get; set; }
    public Uri BaseUrl { get; set; } = new("https://logister.org");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public IDictionary<string, object?> DefaultContext { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public bool CaptureExceptionData { get; set; } = true;
    public string UserAgent { get; set; } = "logister-dotnet/0.1.0";

    public static LogisterOptions FromEnvironment()
    {
        var options = new LogisterOptions
        {
            ApiKey = ReadEnv("LOGISTER_API_KEY"),
            Environment = ReadEnv("LOGISTER_ENVIRONMENT"),
            Release = ReadEnv("LOGISTER_RELEASE")
        };

        var baseUrl = ReadEnv("LOGISTER_BASE_URL");
        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedBaseUrl))
        {
            options.BaseUrl = parsedBaseUrl;
        }

        var timeout = ReadEnv("LOGISTER_TIMEOUT");
        if (double.TryParse(timeout, out var timeoutSeconds) && timeoutSeconds > 0)
        {
            options.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        }

        return options;
    }

    private static string? ReadEnv(string name)
    {
        var value = System.Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
