using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPiConsole.Core.Link;

// Converters that keep hostile or newer-than-us JSON from getting past a
// decode as something it isn't. Everything here fails with JsonException, the
// one exception a session loop is expected to catch.

/// <summary>
/// A closed vocabulary (roles, auth modes, command classes): exactly the wire
/// spellings of its members. Integers, comma lists, padding and unknown names
/// are refused, so a value outside the enum can never reach a role check.
/// </summary>
public sealed class LinkStrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByName = LinkEnumNames<T>.ByName;
    private static readonly Dictionary<T, string> ByValue = LinkEnumNames<T>.ByValue;

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"A {typeof(T).Name} is a string.");
        var text = reader.GetString()!;
        return ByName.TryGetValue(text, out var value) ? value : throw new JsonException($"Unknown {typeof(T).Name} \"{text}\".");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ByValue.TryGetValue(value, out var name) ? name
            : throw new JsonException($"{typeof(T).Name} {value} has no wire spelling."));
}

/// <summary>
/// An open vocabulary (device state, hub kind, link kind, firmware phase): a
/// newer peer may send a value this build doesn't know, and that must not
/// fail the whole message (or a whole device list). An unknown spelling reads
/// as the enum's Unknown member; Unknown is never written.
/// </summary>
public sealed class LinkOpenEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> ByName = LinkEnumNames<T>.ByName;
    private static readonly Dictionary<T, string> ByValue = LinkEnumNames<T>.ByValue;
    private static readonly T Unknown = Enum.Parse<T>("Unknown");

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"A {typeof(T).Name} is a string.");
        return ByName.TryGetValue(reader.GetString()!, out var value) ? value : Unknown;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ByValue.TryGetValue(value, out var name) ? name
            : throw new JsonException($"{typeof(T).Name} {value} can't be sent."));
}

/// <summary>Wire spellings from each member's JsonStringEnumMemberName.</summary>
internal static class LinkEnumNames<T> where T : struct, Enum
{
    public static readonly Dictionary<string, T> ByName = new(StringComparer.Ordinal);
    public static readonly Dictionary<T, string> ByValue = new();

    static LinkEnumNames()
    {
        foreach (var field in typeof(T).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is not { } attr) continue;
            var value = (T)field.GetValue(null)!;
            ByName[attr.Name] = value;
            ByValue[value] = attr.Name;
        }
    }
}

/// <summary>ISO-8601 timestamps (spec 7.1): written UTC with a "Z", read in
/// any ISO-8601 date-time form. Anything else, or a date out of range, is a
/// JsonException.</summary>
internal sealed class LinkTimestampConverter : JsonConverter<DateTimeOffset>
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
    ];

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A timestamp is a string.");
        var text = reader.GetString()!;
        try
        {
            if (DateTimeOffset.TryParseExact(text, Formats, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                return value;
        }
        catch (ArgumentException)
        {
            // An offset that pushes the instant outside DateTimeOffset's range.
        }
        throw new JsonException($"Not an ISO-8601 timestamp: \"{text}\".");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
}

/// <summary>JSON has no infinity or NaN: 1e400 is refused on the way in, and
/// a non-finite value (a 0/0 average) goes out as 0 rather than failing the
/// whole reply.</summary>
internal sealed class LinkFiniteDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number.");
        var value = reader.GetDouble();
        return double.IsFinite(value) ? value : throw new JsonException("A number out of range.");
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(double.IsFinite(value) ? value : 0);
}

/// <summary>
/// Lists of objects or strings: a null element is refused, since nothing in
/// the protocol puts one there and every consumer would trip over it later
/// (RespectNullableAnnotations doesn't reach list elements).
/// </summary>
internal sealed class LinkNonNullListConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)
        && !type.GetGenericArguments()[0].IsValueType;

    public override JsonConverter CreateConverter(Type type, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(Converter<>).MakeGenericType(type.GetGenericArguments()[0]))!;

    private sealed class Converter<T> : JsonConverter<IReadOnlyList<T>> where T : class
    {
        public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array.");
            var list = new List<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.Null) throw new JsonException("A list holds a null.");
                list.Add(JsonSerializer.Deserialize<T>(ref reader, options) ?? throw new JsonException("A list holds a null."));
            }
            return list;
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options) =>
            // As an array type, so this converter isn't asked again.
            JsonSerializer.Serialize(writer, value.ToArray(), options);
    }
}
