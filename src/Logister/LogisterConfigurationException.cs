namespace Logister;

/// <summary>Thrown when Logister is missing required configuration or a configured value is invalid.</summary>
public sealed class LogisterConfigurationException : ArgumentException
{
    public LogisterConfigurationException(string message)
        : base(message, "options")
    {
    }
}
