using System.Collections;

namespace Logister;

internal static class LogisterValueNormalizer
{
    public static object? Normalize(object? value)
    {
        if (value is null)
        {
            return null;
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

        if (value is Exception exception)
        {
            return LogisterExceptionPayload.FromException(exception, includeData: true);
        }

        if (value is IDictionary<string, object?> stringDictionary)
        {
            return stringDictionary.ToDictionary(
                pair => pair.Key,
                pair => Normalize(pair.Value),
                StringComparer.OrdinalIgnoreCase);
        }

        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (DictionaryEntry entry in dictionary)
            {
                var key = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    result[key] = Normalize(entry.Value);
                }
            }

            return result;
        }

        if (value is IEnumerable enumerable)
        {
            var result = new List<object?>();
            foreach (var item in enumerable)
            {
                result.Add(Normalize(item));
            }

            return result;
        }

        return value.ToString();
    }
}
