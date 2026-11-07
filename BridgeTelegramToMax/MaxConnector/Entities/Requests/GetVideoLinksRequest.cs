namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record GetVideoLinksRequest(long ChatId, long MessageId, string? Token, long? VideoId);
