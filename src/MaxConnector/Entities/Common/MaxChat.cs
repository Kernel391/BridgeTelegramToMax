namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Чат. Type: DIALOG, CHAT или CHANNEL. У личных чатов названия нет.
/// </summary>
public sealed record MaxChat(long Id, string? Type, string? Title);
