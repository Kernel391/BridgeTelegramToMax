namespace BridgeTelegramToMax.MaxConnector.Entities.Notifications;

/// <summary>
/// Уведомление сервера (opcode 136): загруженный файл обработан, его можно прикреплять к сообщению.
/// </summary>
internal sealed record FileReadyNotification(long FileId);
