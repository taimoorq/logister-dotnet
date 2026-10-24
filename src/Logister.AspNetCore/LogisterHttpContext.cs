using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace Logister.AspNetCore;

internal static partial class LogisterHttpContext
{
    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x-functions-key",
        "x-goog-iap-jwt-assertion",
        "cf-access-jwt-assertion",
        "x-amzn-oidc-data",
        "x-amzn-oidc-identity",
        "x-hub-signature",
        "x-hub-signature-256",
        "stripe-signature",
        "x-csrf-token",
        "x-xsrf-token",
        "x-requestverificationtoken"
    };

    private static readonly string[] SensitiveHeaderPrefixes =
    {
        "x-ms-client-principal",
        "x-ms-token-"
    };

    private static readonly HashSet<string> ClientAddressHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "forwarded",
        "x-forwarded-for",
        "x-original-forwarded-for",
        "x-real-ip",
        "x-client-ip",
        "x-cluster-client-ip",
        "cf-connecting-ip",
        "true-client-ip",
        "fastly-client-ip",
        "x-azure-clientip",
        "x-azure-socketip"
    };

    private static readonly string[] SensitiveCookiePrefixes =
    {
        ".AspNetCore.",
        "__Host-",
        "__Secure-"
    };

    public static IDictionary<string, object?> BuildContext(
        HttpContext context,
        LogisterAspNetCoreOptions options,
        double? durationMs = null,
        int? statusCode = null,
        LogisterValueSanitizer? sanitizer = null)
    {
        sanitizer ??= new LogisterValueSanitizer(options.Client.RedactKeys);
        var request = context.Request;
        var sensitiveSegments = SensitiveRouteSegments(request, sanitizer);
        var routeValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in request.RouteValues)
        {
            routeValues[key] = sanitizer.IsSensitiveKey(key) ? LogisterValueSanitizer.Redacted : value?.ToString();
        }

        var capturedStatusCode = statusCode ?? context.Response.StatusCode;
        var path = RedactSegments(request.Path.Value, sensitiveSegments);

        var requestContext = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["method"] = request.Method,
            ["path"] = path,
            ["query_string"] = request.QueryString.HasValue ? sanitizer.RedactQueryString(request.QueryString.Value!) : null,
            ["url"] = sanitizer.RedactUrl(RedactSegments(BuildDisplayUrl(request), sensitiveSegments)!),
            ["request_id"] = context.TraceIdentifier,
            ["span_id"] = LogisterTraceContext.Current?.SpanId,
            ["parent_span_id"] = LogisterTraceContext.Current?.ParentSpanId,
            ["trace_id"] = LogisterTraceContext.Current?.TraceId,
            ["client_ip"] = options.CaptureClientIp ? context.Connection.RemoteIpAddress?.ToString() : null,
            ["user_agent"] = request.Headers.UserAgent.ToString(),
            ["route"] = routeValues.Count > 0 ? routeValues : null,
            ["endpoint"] = context.GetEndpoint()?.DisplayName,
            ["status"] = capturedStatusCode
        };

        if (options.CaptureRequestHeaders)
        {
            var headers = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                if (!options.CaptureClientIp && ClientAddressHeaders.Contains(header.Key)) continue;
                headers[header.Key] = IsSensitiveHeader(header.Key, options, sanitizer)
                    ? LogisterValueSanitizer.Redacted
                    : header.Key.Equals("referer", StringComparison.OrdinalIgnoreCase)
                        ? sanitizer.RedactUrl(header.Value.ToString())
                        : sanitizer.ScrubString(header.Value.ToString());
            }

            requestContext["headers"] = headers;
        }

        if (options.CaptureRequestCookies && request.Cookies.Count > 0)
        {
            var cookies = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var cookie in request.Cookies)
            {
                cookies[cookie.Key] = CookieValue(cookie.Key, cookie.Value, options, sanitizer);
            }

            requestContext["cookies"] = cookies;
        }

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["framework"] = "aspnetcore",
            ["runtime"] = ".NET",
            ["request"] = requestContext
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            ["route"] = context.GetEndpoint()?.DisplayName ?? path,
            ["request_id"] = context.TraceIdentifier,
            ["span_id"] = LogisterTraceContext.Current?.SpanId,
            ["parent_span_id"] = LogisterTraceContext.Current?.ParentSpanId,
            ["trace_id"] = LogisterTraceContext.Current?.TraceId
        };

        if (durationMs is not null)
        {
            result["duration_ms"] = durationMs.Value;
        }

        return result
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>A display name that never contains raw sensitive route values.</summary>
    public static string TransactionName(HttpContext context, LogisterValueSanitizer sanitizer)
    {
        var endpoint = context.GetEndpoint()?.DisplayName;
        if (!string.IsNullOrWhiteSpace(endpoint)) return endpoint;
        var path = RedactSegments(context.Request.Path.Value, SensitiveRouteSegments(context.Request, sanitizer));
        return $"{context.Request.Method} {sanitizer.ScrubString(string.IsNullOrEmpty(path) ? "/" : path)}".TrimStart();
    }

    private static HashSet<string> SensitiveRouteSegments(HttpRequest request, LogisterValueSanitizer sanitizer)
    {
        var segments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.RouteValues)
        {
            var text = sanitizer.IsSensitiveKey(key) ? value?.ToString() : null;
            if (!string.IsNullOrEmpty(text)) segments.Add(Uri.EscapeDataString(text));
        }

        return segments;
    }

    internal static bool IsSensitiveHeader(string name, LogisterAspNetCoreOptions options, LogisterValueSanitizer sanitizer)
    {
        if (sanitizer.IsSensitiveKey(name) || SensitiveHeaders.Contains(name) || options.SensitiveRequestHeaderNames.Contains(name))
            return true;
        foreach (var prefix in SensitiveHeaderPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal static bool IsSensitiveCookie(string name, LogisterAspNetCoreOptions options, LogisterValueSanitizer sanitizer)
    {
        // ASP.NET Core splits large cookies into name + nameC1, nameC2…; each chunk is as sensitive as the whole.
        var baseName = CookieChunkSuffix().Replace(name, string.Empty);
        foreach (var candidate in new[] { name, baseName })
        {
            if (candidate.Length == 0) continue;
            if (options.SensitiveRequestCookieNames.Contains(candidate) || sanitizer.IsSensitiveKey(candidate)) return true;
            foreach (var prefix in SensitiveCookiePrefixes)
                if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            var normalized = LogisterValueSanitizer.NormalizeKey(candidate);
            if (normalized.Contains("antiforgery", StringComparison.Ordinal) ||
                normalized.Contains("xsrf", StringComparison.Ordinal) ||
                normalized.Contains("csrf", StringComparison.Ordinal) ||
                normalized.Contains("session", StringComparison.Ordinal) ||
                normalized.Contains("auth", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string CookieValue(string name, string value, LogisterAspNetCoreOptions options, LogisterValueSanitizer sanitizer)
    {
        return options.AllowedRequestCookieNames.Contains(name) && !IsSensitiveCookie(name, options, sanitizer)
            ? sanitizer.ScrubString(value)
            : options.RedactedCookieValue;
    }

    private static string? RedactSegments(string? value, HashSet<string> sensitiveSegments)
    {
        if (string.IsNullOrEmpty(value) || sensitiveSegments.Count == 0) return value;
        var query = value.IndexOfAny(new[] { '?', '#' });
        var pathPart = query < 0 ? value : value[..query];
        var segments = pathPart.Split('/');
        for (var index = 0; index < segments.Length; index++)
        {
            if (sensitiveSegments.Contains(segments[index]) ||
                sensitiveSegments.Contains(Uri.EscapeDataString(SafeUnescape(segments[index]))))
                segments[index] = LogisterValueSanitizer.Redacted;
        }

        return string.Join('/', segments) + (query < 0 ? string.Empty : value[query..]);
    }

    private static string SafeUnescape(string value)
    {
        try { return Uri.UnescapeDataString(value); }
        catch (UriFormatException) { return value; }
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

    [GeneratedRegex(@"C\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex CookieChunkSuffix();
}
