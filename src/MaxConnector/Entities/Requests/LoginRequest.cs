namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record LoginRequest(
    string Token,
    int ChatsCount,
    bool Interactive,
    int ChatsSync,
    int ContactsSync,
    int PresenceSync,
    int DraftsSync);
