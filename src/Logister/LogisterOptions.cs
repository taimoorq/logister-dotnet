namespace Logister;

public sealed class LogisterOptions
{
    public string? ApiKey { get; set; }
    public Uri BaseUrl { get; set; } = new("https://logister.org");
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    public string? Environment { get; set; }
    public string? Release { get; set; }
    public string? Repository { get; set; }
    public string? CommitSha { get; set; }
    public string? Branch { get; set; }
    public IDictionary<string, object?> DefaultContext { get; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
    public bool CaptureExceptionData { get; set; } = true;
    public RetryPolicy RetryPolicy { get; set; } = new();
    public string UserAgent { get; set; } = "logister-dotnet/0.4.0";

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
