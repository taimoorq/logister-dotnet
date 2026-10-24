using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace Logister;

internal static class LogisterExceptionPayload
{
    private const int MaxInnerExceptions = 20;

    public static IDictionary<string, object?> FromException(
        Exception exception,
        bool includeData,
        int depth = 0,
        LogisterValueNormalizer.NormalizationState? state = null,
        int valueDepth = 0)
    {
        state ??= new LogisterValueNormalizer.NormalizationState();
        var payload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["class"] = exception.GetType().Name,
            ["qualified_class"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["hresult"] = exception.HResult,
            ["source"] = exception.Source,
            ["target_site"] = FormatMethod(exception.TargetSite),
            ["stack"] = exception.StackTrace
        };

        var frames = BuildFrames(exception);
        if (frames.Count > 0)
        {
            payload["frames"] = frames;
        }

        var backtrace = BuildBacktrace(exception);
        if (backtrace.Count > 0)
        {
            payload["backtrace"] = backtrace;
        }

        if (includeData)
        {
            var data = BuildData(exception, state, valueDepth);
            if (data.Count > 0)
            {
                payload["data"] = data;
            }
        }

        if (exception.InnerException is not null && depth < 3)
        {
            payload["inner_exception"] = FromException(exception.InnerException, includeData, depth + 1, state, valueDepth + 1);
            payload["cause"] = payload["inner_exception"];
        }

        if (exception is AggregateException aggregateException && depth < 3)
        {
            var innerExceptions = aggregateException.InnerExceptions
                .Take(MaxInnerExceptions)
                .Select(inner => FromException(inner, includeData, depth + 1, state, valueDepth + 1))
                .ToArray();

            if (innerExceptions.Length > 0)
            {
                payload["inner_exceptions"] = innerExceptions;
            }
        }

        return payload
            .Where(pair => pair.Value is not null)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static List<IDictionary<string, object?>> BuildFrames(Exception exception)
    {
        var frames = new List<IDictionary<string, object?>>();
        var stackTrace = new StackTrace(exception, fNeedFileInfo: true);

        foreach (var frame in stackTrace.GetFrames() ?? Array.Empty<StackFrame>())
        {
            var method = frame.GetMethod();
            var fileName = frame.GetFileName();
            var lineNumber = frame.GetFileLineNumber();

            var framePayload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["filename"] = fileName,
                ["lineno"] = lineNumber > 0 ? lineNumber : null,
                ["name"] = FormatMethod(method),
                ["declaring_type"] = method?.DeclaringType?.FullName
            };

            if (fileName is not null && lineNumber > 0)
            {
                framePayload["raw"] = $"at {FormatMethod(method)} in {fileName}:line {lineNumber}";
            }

            var compact = framePayload
                .Where(pair => pair.Value is not null)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            if (compact.Count > 0)
            {
                frames.Add(compact);
            }
        }

        return frames;
    }

    private static List<string> BuildBacktrace(Exception exception)
    {
        return exception.StackTrace?
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList() ?? [];
    }

    private static IDictionary<string, object?> BuildData(
        Exception exception,
        LogisterValueNormalizer.NormalizationState state,
        int valueDepth)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        // Mark the exception active so a value in its own Data that refers back to it is reported as circular.
        var entered = state.Enter(exception);
        try
        {
            foreach (DictionaryEntry entry in exception.Data)
            {
                if (result.Count == LogisterValueNormalizer.MaxCollectionItems)
                {
                    result["_truncated"] = true;
                    break;
                }

                var key = entry.Key is string text ? text : LogisterValueNormalizer.SafeToString(entry.Key);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                result[key] = LogisterValueNormalizer.Normalize(entry.Value, state, valueDepth + 1);
            }
        }
        catch (Exception)
        {
            result["_unreadable"] = true;
        }
        finally
        {
            if (entered) state.Exit(exception);
        }

        return result;
    }

    private static string? FormatMethod(MethodBase? method)
    {
        if (method is null)
        {
            return null;
        }

        return method.DeclaringType is null
            ? method.Name
            : $"{method.DeclaringType.FullName}.{method.Name}";
    }
}
