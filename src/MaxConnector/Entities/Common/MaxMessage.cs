using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Сообщение MAX.
/// </summary>
public sealed record MaxMessage
{
    public long Id { get; init; }

    /// <summary>
    /// ID чата. Внутри сообщения MAX его не присылает, поэтому клиент проставляет его сам.
    /// </summary>
    public long ChatId { get; init; }

    /// <summary>
    /// Только числовой ID. Имя можно получить через <see cref="MaxClient.GetUserNameAsync"/>.
    /// </summary>
    [JsonPropertyName("sender")]
    public long? SenderId { get; init; }

    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// ID, который отправивший клиент придумал сам (в протоколе поле cid). По нему узнаём собственную отправку:
    /// <see cref="MaxClient.IsSentByThisClient"/>.
    /// </summary>
    [JsonPropertyName("cid")]
    public long? ClientMessageId { get; init; }

    /// <summary>
    /// Время отправки в миллисекундах (в протоколе поле time).
    /// </summary>
    [JsonPropertyName("time")]
    public long? SentAtMs { get; init; }

    /// <summary>
    /// Новое сообщение, правка или удаление. Для нового сообщения поля в протоколе нет.
    /// </summary>
    public MaxMessageStatus Status { get; init; } = MaxMessageStatus.New;

    [JsonPropertyName("attaches")]
    public List<MaxAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Не null, если это ответ на сообщение или пересылка (в протоколе поле link).
    /// </summary>
    [JsonPropertyName("link")]
    public MaxQuote? Quote { get; init; }

    [JsonIgnore]
    public DateTimeOffset? SentAt => SentAtMs is { } milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;

    [JsonIgnore]
    public bool IsNew => Status == MaxMessageStatus.New;
}
