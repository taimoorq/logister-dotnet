using System.Collections;
using System.Text;
using System.Text.RegularExpressions;

namespace Logister;

/// <summary>
/// Applies Logister's shared default redaction rules to captured values: sensitive keys,
/// Bearer/Basic credentials, JWT-shaped strings, <c>lgst_</c> keys and sensitive URL query parameters.
/// </summary>
public sealed partial class LogisterValueSanitizer
{
    public const string Redacted = "[REDACTED]";

    private static readonly HashSet<string> ExactKeys = new(StringComparer.Ordinal)
    {
        "password", "passwd", "pwd", "secret", "token", "apikey", "authorization", "proxyauthorization",
        "cookie", "setcookie", "sessiontoken", "sessionkey", "credential", "credentials", "privatekey",
        "clientsecret", "accesstoken", "refreshtoken", "idtoken", "authtoken", "xapikey", "xauthtoken",
        "xaccesstoken", "xcsrftoken", "xxsrftoken", "signature", "ssn", "creditcard", "cardnumber", "cvv"
    };

    private static readonly string[] ContainedKeys =
    {
        "token", "secret", "password", "passwd", "apikey", "authorization", "credential", "cookie"
    };

    private static readonly HashSet<string> UrlKeys = new(StringComparer.Ordinal)
    {
        "url", "uri", "href", "querystring", "referer", "referrer"
    };

    // SDK envelope identifiers at the top of the context are never rewritten.
    private static readonly HashSet<string> EnvelopeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "trace_id", "span_id", "parent_span_id", "request_id", "session_id", "uuid", "event_id", "event_type"
    };

    private const int MaxDepth = 32;

    private readonly HashSet<string> _additionalKeys;

    public LogisterValueSanitizer(IEnumerable<string>? additionalKeys = null)
    {
        _additionalKeys = new HashSet<string>(
            (additionalKeys ?? Array.Empty<string>()).Select(NormalizeKey).Where(key => key.Length > 0),
            StringComparer.Ordinal);
    }

    public static LogisterValueSanitizer Default { get; } = new();

    /// <summary>Lowercases a key and removes every non-alphanumeric character.</summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        var builder = new StringBuilder(key.Length);
        foreach (var character in key)
            if (char.IsAsciiLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    public bool IsSensitiveKey(string? key)
    {
        var normalized = NormalizeKey(key);
        if (normalized.Length == 0) return false;
        if (ExactKeys.Contains(normalized) || _additionalKeys.Contains(normalized)) return true;
        foreach (var fragment in ContainedKeys)
            if (normalized.Contains(fragment, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Redacts credentials, tokens and sensitive query parameters embedded in free text.</summary>
    public string ScrubString(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = UserInfoPattern().Replace(value, match => $"{match.Groups[1].Value}{match.Groups[2].Value}:{Redacted}@");
        result = EmbeddedQueryParameterPattern().Replace(result, match =>
            IsSensitiveKey(Unescape(match.Groups[2].Value)) ? $"{match.Groups[1].Value}{match.Groups[2].Value}={Redacted}" : match.Value);
        result = AuthorizationPattern().Replace(result, match => $"{match.Groups[1].Value} {Redacted}");
        result = JwtPattern().Replace(result, Redacted);
        return LogisterKeyPattern().Replace(result, Redacted);
    }

    /// <summary>Keeps scheme, host and path, drops the fragment and redacts sensitive query parameter values.</summary>
    public string RedactUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var fragment = url.IndexOf('#');
        if (fragment >= 0) url = url[..fragment];
        var query = url.IndexOf('?');
        return query < 0
            ? ScrubString(url)
            : ScrubString(url[..query]) + "?" + RedactQueryParameters(url[(query + 1)..]);
    }

    /// <summary>Redacts sensitive parameter values in a query string, with or without its leading <c>?</c>.</summary>
    public string RedactQueryString(string query)
    {
        if (string.IsNullOrEmpty(query)) return query;
        return query[0] == '?' ? "?" + RedactQueryParameters(query[1..]) : RedactQueryParameters(query);
    }

    private string RedactQueryParameters(string query)
    {
        var parts = query.Split('&');
        for (var index = 0; index < parts.Length; index++)
        {
            var separator = parts[index].IndexOf('=');
            var name = separator < 0 ? parts[index] : parts[index][..separator];
            parts[index] = IsSensitiveKey(Unescape(name))
                ? $"{name}={Redacted}"
                : ScrubString(parts[index]);
        }

        return string.Join('&', parts);
    }

    /// <summary>Sanitizes a normalized event context, leaving the SDK's envelope identifiers untouched.</summary>
    internal Dictionary<string, object?> SanitizeContext(IDictionary<string, object?> context)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context)
        {
            result[key] = EnvelopeKeys.Contains(key) && value is not IDictionary and not IList
                ? value
                : SanitizeEntry(key, value, parentKey: null, depth: 0);
        }

        return result;
    }

    /// <summary>Sanitizes a value produced by the normalizer (dictionaries, lists, strings and primitives).</summary>
    internal object? Sanitize(object? value, string? key = null) => Sanitize(value, key, parentKey: null, depth: 0);

    private object? SanitizeEntry(string key, object? value, string? parentKey, int depth)
    {
        // Opt-in request cookies are redacted per cookie by the integration, so the container is kept.
        var cookieContainer = value is IDictionary or IDictionary<string, object?> &&
            string.Equals(key, "cookies", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parentKey, "request", StringComparison.OrdinalIgnoreCase);
        return IsSensitiveKey(key) && !cookieContainer ? Redacted : Sanitize(value, key, parentKey, depth + 1);
    }

    private object? Sanitize(object? value, string? key, string? parentKey, int depth)
    {
        if (depth > MaxDepth) return "[MaxDepth]";
        switch (value)
        {
            case null:
                return null;
            case string text:
                return UrlKeys.Contains(NormalizeKey(key)) ? RedactUrl(text) : ScrubString(text);
            case IDictionary<string, object?> dictionary:
            {
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var (childKey, childValue) in dictionary)
                    result[childKey] = SanitizeEntry(childKey, childValue, key, depth);
                return result;
            }
            case IDictionary dictionary:
            {
                var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var childKey = entry.Key?.ToString();
                    if (!string.IsNullOrWhiteSpace(childKey))
                        result[childKey] = SanitizeEntry(childKey, entry.Value, key, depth);
                }

                return result;
            }
            case ICollection collection:
            {
                var result = new List<object?>(collection.Count);
                foreach (var item in collection) result.Add(Sanitize(item, key, parentKey, depth + 1));
                return result;
            }
            default:
                return value;
        }
    }

    private static string Unescape(string value)
    {
        try { return Uri.UnescapeDataString(value.Replace('+', ' ')); }
        catch (UriFormatException) { return value; }
    }

    [GeneratedRegex(@"\b([a-z][a-z0-9+.\-]*://)([^/\s:@?#]+):([^/\s@?#]+)@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserInfoPattern();

    [GeneratedRegex(@"([?&])([^=&#\s?]+)=([^&#\s]*)", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedQueryParameterPattern();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationPattern();

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]*\.[A-Za-z0-9_\-]+\.[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"lgst_[A-Za-z0-9_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex LogisterKeyPattern();
}
