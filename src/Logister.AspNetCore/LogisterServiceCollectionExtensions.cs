using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.TryAddSingleton(serviceProvider => new LogisterEventQueue(serviceProvider.GetRequiredService<LogisterAspNetCoreOptions>()));
        services.AddHostedService<LogisterDeliveryService>();
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
        SetIfPresent(section, "BaseUrl", value => options.Client.BaseUrl = LogisterOptions.ParseBaseUrl(value, "Logister:BaseUrl"));
        SetIfPresent(section, "AllowInsecureHttp", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.Client.AllowInsecureHttp = parsed;
            }
        });
        SetIfPresent(section, "CaptureHostIdentity", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.Client.CaptureHostIdentity = parsed;
            }
        });
        SetIfPresent(section, "CaptureClientIp", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureClientIp = parsed;
            }
        });
        SetIfPresent(section, "TransactionSampleRate", value =>
        {
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rate))
            {
                throw new LogisterConfigurationException("Logister:TransactionSampleRate must be a number between 0 and 1.");
            }

            options.TransactionSampleRate = rate;
        });
        SetIfPresent(section, "QueueCapacity", value =>
        {
            if (int.TryParse(value, out var capacity) && capacity > 0)
            {
                options.QueueCapacity = capacity;
            }
        });
        AddAll(section, "RedactKeys", options.Client.RedactKeys);
        AddAll(section, "AllowedRequestCookieNames", options.AllowedRequestCookieNames);
        AddAll(section, "SensitiveRequestCookieNames", options.SensitiveRequestCookieNames);
        AddAll(section, "SensitiveRequestHeaderNames", options.SensitiveRequestHeaderNames);
        SetIfPresent(section, "Environment", value => options.Client.Environment = value);
        SetIfPresent(section, "Release", value => options.Client.Release = value);
        SetIfPresent(section, "Repository", value => options.Client.Repository = value);
        SetIfPresent(section, "CommitSha", value => options.Client.CommitSha = value);
        SetIfPresent(section, "Branch", value => options.Client.Branch = value);
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
        SetIfPresent(section, "CaptureRequestSpans", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureRequestSpans = parsed;
            }
        });
        SetIfPresent(section, "CaptureRequestHeaders", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureRequestHeaders = parsed;
            }
        });
        SetIfPresent(section, "CaptureRequestCookies", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.CaptureRequestCookies = parsed;
            }
        });
        SetIfPresent(section, "RedactedCookieValue", value => options.RedactedCookieValue = value);
        SetIfPresent(section, "CaptureExceptionData", value =>
        {
            if (bool.TryParse(value, out var parsed))
            {
                options.Client.CaptureExceptionData = parsed;
            }
        });
    }

    // Accepts either a JSON array or a comma-separated string.
    private static void AddAll(IConfiguration section, string key, ISet<string> target)
    {
        var child = section.GetSection(key);
        var values = child.Value is not null
            ? child.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : child.GetChildren().Select(item => item.Value?.Trim()).Where(item => !string.IsNullOrEmpty(item));
        foreach (var value in values)
        {
            target.Add(value!);
        }
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
