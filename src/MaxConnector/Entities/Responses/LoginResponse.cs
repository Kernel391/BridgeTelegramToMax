using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

internal sealed record LoginResponse
{
    public LoginProfile? Profile { get; init; }

    public List<LoginChat> Chats { get; init; } = [];
}

internal sealed record LoginProfile
{
    public LoginContact? Contact { get; init; }

    public long? Id { get; init; }
}

internal sealed record LoginContact
{
    public long? Id { get; init; }
}

internal sealed record LoginChat
{
    public long Id { get; init; }

    public MaxMessage? LastMessage { get; init; }
}
