using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;

namespace Logister;

/// <summary>
/// Converts captured values into JSON-safe trees. Depth, item count and reference cycles are bounded,
/// and only materialized collections are enumerated so lazy sequences and queries are never executed.
/// </summary>
internal static class LogisterValueNormalizer
{
    internal const int MaxDepth = 12;
    internal const int MaxItems = 5000;
    internal const int MaxCollectionItems = 200;
    internal const string CircularMarker = "[Circular]";
    internal const string MaxDepthMarker = "[MaxDepth]";
    internal const string TruncatedMarker = "[Truncated]";

    private static readonly ConcurrentDictionary<Type, bool> GenericCollectionTypes = new();

    public static object? Normalize(object? value) => Normalize(value, new NormalizationState(), 0);

    internal static object? Normalize(object? value, NormalizationState state, int depth)
    {
        if (value is null)
        {
            return null;
        }

        if (!state.TryTake())
        {
            return TruncatedMarker;
        }

        if (value is double number && !double.IsFinite(number) || value is float single && !float.IsFinite(single))
        {
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        if (value is string or bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            return value;
        }

        if (value is DateTime dateTime)
        {
            return dateTime.ToUniversalTime().ToString("O");
        }

        if (value is DateTimeOffset dateTimeOffset)
        {
            return dateTimeOffset.ToUniversalTime().ToString("O");
        }

        if (value is Guid or Uri or Enum)
        {
            return value.ToString();
        }

        if (depth >= MaxDepth)
        {
            return MaxDepthMarker;
        }

        if (!state.Enter(value))
        {
            return CircularMarker;
        }

        try
        {
            return value switch
            {
                Exception exception => LogisterExceptionPayload.FromException(exception, includeData: true, state: state, valueDepth: depth + 1),
                IDictionary<string, object?> stringDictionary => NormalizeDictionary(stringDictionary, state, depth),
                IDictionary dictionary => NormalizeDictionary(dictionary, state, depth),
                IQueryable queryable => $"[{queryable.GetType().Name}]",
                ICollection collection => NormalizeItems(collection, state, depth),
                IEnumerable enumerable when IsGenericCollection(value.GetType()) => NormalizeItems(enumerable, state, depth),
                _ => SafeToString(value)
            };
        }
        catch (Exception)
        {
            return $"[Unserializable {value.GetType().Name}]";
        }
        finally
        {
            state.Exit(value);
        }
    }

    private static Dictionary<string, object?> NormalizeDictionary(
        IDictionary<string, object?> dictionary,
        NormalizationState state,
        int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, item) in dictionary)
        {
            if (result.Count == MaxCollectionItems)
            {
                result["_truncated"] = true;
                break;
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = Normalize(item, state, depth + 1);
            }
        }

        return result;
    }

    private static Dictionary<string, object?> NormalizeDictionary(IDictionary dictionary, NormalizationState state, int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (result.Count == MaxCollectionItems)
            {
                result["_truncated"] = true;
                break;
            }

            var key = entry.Key is string text ? text : SafeToString(entry.Key);
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = Normalize(entry.Value, state, depth + 1);
            }
        }

        return result;
    }

    private static List<object?> NormalizeItems(IEnumerable enumerable, NormalizationState state, int depth)
    {
        var result = new List<object?>();
        foreach (var item in enumerable)
        {
            if (result.Count == MaxCollectionItems)
            {
                result.Add(TruncatedMarker);
                break;
            }

            result.Add(Normalize(item, state, depth + 1));
        }

        return result;
    }

    private static bool IsGenericCollection(Type type) =>
        GenericCollectionTypes.GetOrAdd(type, static candidate => candidate.GetInterfaces().Any(contract =>
            contract.IsGenericType &&
            (contract.GetGenericTypeDefinition() == typeof(ICollection<>) ||
             contract.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>))));

    internal static string SafeToString(object? value)
    {
        if (value is null) return string.Empty;
        try
        {
            return value.ToString() ?? value.GetType().Name;
        }
        catch (Exception)
        {
            return $"[{value.GetType().Name}]";
        }
    }

    internal sealed class NormalizationState
    {
        private readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);
        private int _remaining = MaxItems;

        public bool TryTake() => _remaining-- > 0;

        public bool Enter(object value) => value.GetType().IsValueType || _active.Add(value);

        public void Exit(object value)
        {
            if (!value.GetType().IsValueType) _active.Remove(value);
        }
    }
}
