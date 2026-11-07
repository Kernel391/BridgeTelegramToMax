namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

internal sealed record GetFileLinkResponse
{
    public string? Url { get; init; }
}
