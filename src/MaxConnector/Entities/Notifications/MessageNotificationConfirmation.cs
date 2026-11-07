namespace BridgeTelegramToMax.MaxConnector.Entities.Notifications;

/// <summary>
/// Подтверждение уведомления о сообщении (отправляется в ответ на кадр от сервера).
/// </summary>
internal sealed record MessageNotificationConfirmation(long ChatId, long MessageId);
