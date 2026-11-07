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
    /// Время отправки в миллисекундах (в протоколе поле time).
    /// </summary>
    [JsonPropertyName("time")]
    public long? SentAtMs { get; init; }

    /// <summary>
    /// null для нового сообщения, "EDITED" или "REMOVED" для правки и удаления.
    /// </summary>
    public string? Status { get; init; }

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
    public bool IsNew => Status is null;
}
