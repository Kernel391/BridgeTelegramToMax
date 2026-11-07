using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

internal sealed record GetChatHistoryResponse
{
    public List<MaxMessage> Messages { get; init; } = [];
}
