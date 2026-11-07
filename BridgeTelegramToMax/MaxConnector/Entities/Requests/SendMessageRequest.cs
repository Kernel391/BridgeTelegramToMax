using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record SendMessageRequest(long ChatId, OutgoingMessage Message, bool Notify);

internal sealed record OutgoingMessage
{
    /// <summary>
    /// ID сообщения, который клиент придумывает сам (в протоколе поле cid).
    /// </summary>
    [JsonPropertyName("cid")]
    public required long ClientMessageId { get; init; }

    public required string Text { get; init; }

    public object[] Elements { get; init; } = [];

    public object[] Attaches { get; init; } = [];

    /// <summary>
    /// Ответ на сообщение (в протоколе поле link).
    /// </summary>
    [JsonPropertyName("link")]
    public OutgoingQuote? Quote { get; init; }
}


internal sealed record OutgoingQuote(string Type, long MessageId);