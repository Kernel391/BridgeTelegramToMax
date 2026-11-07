using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

/// <summary>
/// Ответ на отправку сообщения: ID чата и отправленное сообщение.
/// </summary>
internal sealed record MessageResponse
{
    public long ChatId { get; init; }

    public MaxMessage? Message { get; init; }
}
