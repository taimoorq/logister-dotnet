using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Logister.AspNetCore;

internal static class LogisterHttpContext
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "authorization",
        "cookie",
        "set-cookie",
        "x-api-key"
    };

    public static IDictionary<string, object?> BuildContext(
        HttpContext context,
        LogisterAspNetCoreOptions options,
        double? durationMs = null,
        int? statusCode = null)
    {
        var request = context.Request;
        var routeValues = request.RouteValues.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value?.ToString(),
            StringComparer.OrdinalIgnoreCase);
        var capturedStatusCode = statusCode ?? context.Response.StatusCode;

        var requestContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = request.Method,
            ["path"] = request.Path.Value,
            ["query_string"] = request.QueryString.HasValue ? request.QueryString.Value : null,
            ["url"] = BuildDisplayUrl(request),
            ["request_id"] = context.TraceIdentifier,
            ["trace_id"] = Activity.Current?.TraceId.ToString(),
            ["client_ip"] = context.Connection.RemoteIpAddress?.ToString(),
            ["user_agent"] = request.Headers.UserAgent.ToString(),
            ["route"] = routeValues.Count > 0 ? routeValues : null,
            ["endpoint"] = context.GetEndpoint()?.DisplayName,
            ["status"] = capturedStatusCode
        };

        if (options.CaptureRequestHeaders)
        {
            requestContext["headers"] = request.Headers
                .Where(header => !SensitiveHeaders.Contains(header.Key))
                .ToDictionary(
                    header => header.Key,
                    header => (object?)header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
        }

        if (options.CaptureRequestCookies && request.Cookies.Count > 0)
        {
            requestContext["cookies"] = request.Cookies.ToDictionary(
                cookie => cookie.Key,
                cookie => (object?)CookieValue(cookie.Key, cookie.Value, options),
                StringComparer.OrdinalIgnoreCase);
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["framework"] = "aspnetcore",
            ["runtime"] = ".NET",
            ["request"] = requestContext
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            ["route"] = context.GetEndpoint()?.DisplayName ?? request.Path.Value,
            ["request_id"] = context.TraceIdentifier,
            ["trace_id"] = Activity.Current?.TraceId.ToString()
        };

        if (durationMs is not null)
        {
            result["duration_ms"] = durationMs.Value;
        }

        return result
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static string CookieValue(string name, string value, LogisterAspNetCoreOptions options)
    {
        return options.SensitiveRequestCookieNames.Contains(name)
            ? options.RedactedCookieValue
            : value;
    }

    private static string BuildDisplayUrl(HttpRequest request)
    {
        return string.Concat(
            request.Scheme,
            "://",
            request.Host.ToUriComponent(),
            request.PathBase.ToUriComponent(),
            request.Path.ToUriComponent(),
            request.QueryString.ToUriComponent());
    }
}
