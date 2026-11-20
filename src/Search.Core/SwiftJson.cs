using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Search.Core;

// JSON as the Mac's Foundation JSONEncoder writes it, so a session or history file can move between a Mac and a PC.
//
// A Date is a number: seconds since 2001-01-01T00:00:00Z. A UUID is an uppercase string. A missing optional is a
// missing key, and null reads as missing. "/" is written "\/", as Foundation does by default, and nothing outside
// ASCII is escaped. Read by hand rather than by reflection, so the reader can be as lenient, key by key, as the
// Swift decoder it stands in for, and nothing here gets in the way of trimming.
public static class SwiftJson
{
    /// Foundation's reference date, where a Date's seconds count from.
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly double Earliest = (DateTimeOffset.MinValue - ReferenceDate).TotalSeconds;
    private static readonly double Latest = (DateTimeOffset.MaxValue - ReferenceDate).TotalSeconds;

    public static double Seconds(DateTimeOffset date) => (date - ReferenceDate).Ticks / (double)TimeSpan.TicksPerSecond;

    /// Nil for a number no DateTimeOffset can hold (Foundation would take it; nothing real is out there).
    public static DateTimeOffset? Date(double seconds) =>
        double.IsFinite(seconds) && seconds > Earliest && seconds < Latest ? ReferenceDate.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond)) : null;

    public static string Uuid(Guid id) => id.ToString("D").ToUpperInvariant();

    /// As UUID(uuidString:): the hyphenated 36 characters, either case.
    public static Guid? Uuid(string? text) => Guid.TryParseExact(text, "D", out var id) ? id : null;

    // Reading. `Has` is decodeIfPresent's notion of there: a key with null in it isn't.

    public static bool Has(JsonElement obj, string key, out JsonElement value) =>
        obj.TryGetProperty(key, out value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    public static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var n) ? n : null;
    public static long? Long(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n : null;
    public static int? Int(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n : null;
    public static bool? Bool(JsonElement value) => value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    public static DateTimeOffset? Date(JsonElement value) => Number(value) is { } seconds ? Date(seconds) : null;
    public static Guid? Uuid(JsonElement value) => Uuid(Text(value));

    // Writing.

    public static void WriteDate(Utf8JsonWriter writer, string key, DateTimeOffset date) => writer.WriteNumber(key, Seconds(date));
    public static void WriteUuid(Utf8JsonWriter writer, string key, Guid id) => writer.WriteString(key, Uuid(id));

    /// The bytes a Mac would write: compact, raw UTF-8, slashes escaped.
    public static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(writer);
        return EscapeSlashes(buffer.ToArray());
    }

    /// "/" only ever appears inside a string in JSON, so every one can be escaped without looking.
    internal static byte[] EscapeSlashes(byte[] json)
    {
        var slashes = json.AsSpan().Count((byte)'/');
        if (slashes == 0) return json;
        var escaped = new byte[json.Length + slashes];
        var at = 0;
        foreach (var b in json)
        {
            if (b == '/') escaped[at++] = (byte)'\\';
            escaped[at++] = b;
        }
        return escaped;
    }

    /// For System.Text.Json callers: a Date as Foundation writes it.
    public sealed class DateConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.Number && Date(reader.GetDouble()) is { } date ? date : throw new JsonException("Not a Foundation date.");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteNumberValue(Seconds(value));
    }

    /// For System.Text.Json callers: a UUID as Foundation writes it.
    public sealed class UuidConverter : JsonConverter<Guid>
    {
        public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Uuid(reader.GetString()) is { } id ? id : throw new JsonException("Not a UUID.");

        public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) => writer.WriteStringValue(Uuid(value));
    }
}
