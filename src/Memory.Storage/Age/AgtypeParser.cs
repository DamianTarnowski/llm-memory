using System.Text.Json;

namespace Memory.Storage.Age;

internal static class AgtypeParser
{
    private static readonly char[] _typeTagChars = ['v', 'e', 'r', 't', 'x', 'g', 'p', 'a', 'h', 'n', 'u', 'm', 'i', 'c'];

    public static JsonElement Parse(string raw)
    {
        var trimmed = raw.AsSpan().Trim();
        if (trimmed.IsEmpty)
        {
            return JsonDocument.Parse("null").RootElement;
        }

        var stripped = StripTypeSuffix(trimmed);
        return JsonDocument.Parse(stripped.ToString()).RootElement;
    }

    public static T? Deserialize<T>(string raw, JsonSerializerOptions? options = null)
    {
        var stripped = StripTypeSuffix(raw.AsSpan().Trim()).ToString();
        return JsonSerializer.Deserialize<T>(stripped, options ?? JsonOpts.Web);
    }

    private static ReadOnlySpan<char> StripTypeSuffix(ReadOnlySpan<char> s)
    {
        for (var i = s.Length - 1; i >= 1; i--)
        {
            if (s[i] != ':' || s[i - 1] != ':') continue;

            var tail = s[(i + 1)..];
            if (IsKnownTypeTag(tail))
            {
                return s[..(i - 1)];
            }
        }
        return s;
    }

    private static bool IsKnownTypeTag(ReadOnlySpan<char> tail) =>
        tail.SequenceEqual("vertex") ||
        tail.SequenceEqual("edge") ||
        tail.SequenceEqual("path") ||
        tail.SequenceEqual("numeric");

    public static class JsonOpts
    {
        public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    }
}
