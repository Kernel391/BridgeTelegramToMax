using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Что произошло с сообщением. В протоколе это поле status: у нового сообщения его нет, у правки там EDITED,
/// у удаления REMOVED.
/// </summary>
[JsonConverter(typeof(MaxMessageStatusConverter))]
public enum MaxMessageStatus
{
    /// <summary>
    /// Новое сообщение (поля status нет).
    /// </summary>
    New,

    /// <summary>
    /// Сообщение исправлено (EDITED).
    /// </summary>
    Edited,

    /// <summary>
    /// Сообщение удалено (REMOVED).
    /// </summary>
    Removed,

    /// <summary>
    /// Статус, которого мы не знаем. Такое сообщение не считается новым и пересылать его не нужно.
    /// </summary>
    Unknown,
}

/// <summary>
/// Читает и пишет <see cref="MaxMessageStatus"/> как строку протокола. Отсутствующее поле и null читаются как
/// <see cref="MaxMessageStatus.New"/>, неизвестная строка как <see cref="MaxMessageStatus.Unknown"/>.
/// </summary>
public sealed class MaxMessageStatusConverter : JsonConverter<MaxMessageStatus>
{
    // Без этого null в поле status при чтении привёл бы к ошибке: у enum нет значения null.
    public override bool HandleNull => true;

    public override MaxMessageStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String
            ? reader.GetString() switch
            {
                "EDITED" => MaxMessageStatus.Edited,
                "REMOVED" => MaxMessageStatus.Removed,
                _ => MaxMessageStatus.Unknown,
            }
            : MaxMessageStatus.New;

    public override void Write(Utf8JsonWriter writer, MaxMessageStatus value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case MaxMessageStatus.Edited:
                writer.WriteStringValue("EDITED");
                break;
            case MaxMessageStatus.Removed:
                writer.WriteStringValue("REMOVED");
                break;
            case MaxMessageStatus.Unknown:
                writer.WriteStringValue("UNKNOWN");
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
