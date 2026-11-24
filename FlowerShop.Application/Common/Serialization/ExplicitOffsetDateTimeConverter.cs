using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlowerShop.Application.Common.Serialization;

// Only used for voucher request fields; no global JSON configuration change.
public sealed class ExplicitOffsetDateTimeConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return null;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("VOUCHER_DATETIME_INVALID");

        var text = reader.GetString();
        if (text is null || !text.Contains('T') ||
            !(text.EndsWith('Z') ||
              (text.Length >= 6 && (text[^6] is '+' or '-') && text[^3] == ':')))
        {
            throw new JsonException("VOUCHER_DATETIME_OFFSET_REQUIRED");
        }

        if (!reader.TryGetDateTimeOffset(out var value))
            throw new JsonException("VOUCHER_DATETIME_INVALID");

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
