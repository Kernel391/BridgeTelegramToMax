namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record GetFileLinkRequest(long ChatId, long MessageId, long? FileId);
