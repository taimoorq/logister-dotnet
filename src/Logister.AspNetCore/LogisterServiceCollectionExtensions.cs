using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Logister.AspNetCore;

public static class LogisterServiceCollectionExtensions
{
    public static IServiceCollection AddLogister(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<LogisterAspNetCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new LogisterAspNetCoreOptions();
        ApplyConfiguration(options, configuration.GetSection("Logister"));
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton(options.Client);
        services.AddHttpClient<LogisterClient>((serviceProvider, httpClient) =>
        {
            var clientOptions = serviceProvider.GetRequiredService<LogisterOptions>();
            if (clientOptions.Timeout > TimeSpan.Zero)
            {
                httpClient.Timeout = clientOptions.Timeout;
            }
        });

        return services;
    }

    private static void ApplyConfiguration(LogisterAspNetCoreOptions options, IConfiguration section)
    {
        SetIfPresent(section, "ApiKey", value => options.Client.ApiKey = value);
        SetIfPresent(section, "BaseUrl", value =>
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                options.Client.BaseUrl = uri;
            }
        });
        SetIfPresent(section, "Environment", value => options.Client.Environment = value);
        SetIfPresent(section, "Release", value => options.Client.Release = value);
        SetIfPresent(section, "UserAgent", value => options.Client.UserAgent = value);
        SetIfPresent(section, "TimeoutSeconds", value =>
        {
            if (double.TryParse(value, out var seconds) && seconds > 0)
            {
                options.Client.Timeout = TimeSpan.FromSeconds(seconds);
            }
        });
        SetIfPresent(section, "CaptureRequestTransactions", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureRequestTransactions = parsed;
            }
        });
        SetIfPresent(section, "CaptureRequestHeaders", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureRequestHeaders = parsed;
            }
        });
        SetIfPresent(section, "CaptureExceptionData", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.Client.CaptureExceptionData = parsed;
            }
        });
    }

    private static void SetIfPresent(IConfiguration section, string key, Action<string> apply)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value.Trim());
        }
    }
}
