namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Тема чата MAX в Telegram.
/// </summary>
public sealed class ChatTopic
{
    /// <summary>
    /// ID темы (message_thread_id).
    /// </summary>
    public int TopicId { get; set; }

    /// <summary>
    /// Название, с которым тема создана.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// True, если настоящее название чата ещё не известно и в теме стоит заглушка вида «Чат 123».
    /// </summary>
    public bool IsTitlePlaceholder { get; set; }

    /// <summary>
    /// ID последнего пересланного нового сообщения этого чата.
    /// </summary>
    public long LastForwardedMessageId { get; set; }
}
