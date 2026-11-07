namespace BridgeTelegramToMax.MaxConnector;

/// <summary>
/// Номера операций протокола веб-клиента MAX, которые использует клиент.
/// </summary>
public static class MaxOpcode
{
    public const int Ping = 1;
    public const int Handshake = 6;
    public const int Login = 19;
    public const int GetContacts = 32;
    public const int GetChats = 48;
    public const int GetChatHistory = 49;
    public const int SendMessage = 64;
    public const int GetVideoLinks = 83;
    public const int GetFileLink = 88;

    /// <summary>
    /// Уведомление о новом, изменённом или удалённом сообщении.
    /// </summary>
    public const int MessageNotification = 128;
}
