using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Читает значение как строку, даже если сервер прислал число (например, номер телефона без кавычек). Без этого
/// неожиданное число в поле-строке сломало бы разбор всего сообщения. null остаётся null.
/// </summary>
public sealed class FlexibleStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            _ => null,
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
