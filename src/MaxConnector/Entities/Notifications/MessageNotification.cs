using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Notifications;

internal sealed record MessageNotification
{
    public long ChatId { get; init; }

    public MaxMessage? Message { get; init; }
}
