namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record GetChatHistoryRequest(long ChatId, long From, int Forward, int Backward, bool GetMessages);
