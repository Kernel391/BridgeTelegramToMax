using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

internal sealed record GetChatsResponse
{
    public List<MaxChat> Chats { get; init; } = [];
}
