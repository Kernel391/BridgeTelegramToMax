namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

internal sealed record GetContactsResponse
{
    public List<ContactInfo> Contacts { get; init; } = [];
}
internal sealed record ContactInfo
{
    public List<ContactName> Names { get; init; } = [];
}

internal sealed record ContactName
{
    public string? Name { get; init; }
}