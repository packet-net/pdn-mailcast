using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mailcast.HeadEnd.Status;

/// <summary>
/// Writes a NaN or an infinity in a <c>double?</c> as JSON null, so the saved slot report and
/// <c>/status</c> are plain JSON any reader takes. Every double in <see cref="Slot.SlotReport"/>
/// and the ionosonde reading is nullable, so null reads back as "no value", which is what a
/// non-finite one meant. Reading also takes the strings "NaN", "Infinity" and "-Infinity" that
/// reports saved before this was written, as null.
/// </summary>
internal sealed class FiniteOrNullConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetDouble() is var v && double.IsFinite(v) ? v : null,
            JsonTokenType.String => reader.GetString() is "NaN" or "Infinity" or "-Infinity"
                ? null
                : throw new JsonException("expected a number or null"),
            _ => throw new JsonException("expected a number or null"),
        };

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is { } v && double.IsFinite(v))
        {
            writer.WriteNumberValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
