using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Цитата: сообщение является ответом на другое сообщение или пересылкой. Цитируемое сообщение лежит в
/// <see cref="Message"/> (у него самого цитаты нет).
/// </summary>
public sealed record MaxQuote
{
    public const string Reply = "REPLY";
    public const string Forward = "FORWARD";

    /// <summary>
    /// <see cref="Reply"/> или <see cref="Forward"/> (в протоколе поле type).
    /// </summary>
    [JsonPropertyName("type")]
    public string Kind { get; init; } = "UNKNOWN";

    public MaxMessage Message { get; init; } = new();

    /// <summary>
    /// Для пересылки: название чата, откуда переслано.
    /// </summary>
    public string? ChatName { get; init; }

    /// <summary>
    /// Чат цитируемого сообщения. Для пересылки это исходный чат, для ответа тот же, что у ответа.
    /// </summary>
    public long? ChatId { get; init; }
}
