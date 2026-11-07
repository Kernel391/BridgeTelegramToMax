using System.ComponentModel.DataAnnotations;

namespace BridgeTelegramToMax.Configuration;

/// <summary>
/// Настройки Telegram-стороны моста.
/// </summary>
public class TelegramConfig
{
    public const string SectionName = "TelegramConfig";

    /// <summary>
    /// Токен бота от @BotFather.
    /// </summary>
    [Required]
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>
    /// ID супергруппы с включёнными темами (Topics), куда бот пересылает сообщения. Это отрицательное число
    /// вида -100xxxxxxxxxx. Бот должен быть администратором с правом управлять темами.
    /// </summary>
    [Required]
    public long? GroupChatId { get; set; }

    /// <summary>
    /// Максимальный размер файла, который бот отправляет в Telegram, в мегабайтах. Всё, что больше, приходит
    /// в тему ссылкой на файл в MAX. По умолчанию 50 (лимит официального Bot API), для своего сервера Bot API
    /// можно поставить до 2000.
    /// </summary>
    [Range(1, 2000)]
    public int MaxUploadMegabytes { get; set; } = 50;

    /// <summary>
    /// Файл, где хранится соответствие чатов MAX и тем Telegram. Нужен потому, что Bot API не умеет
    /// перечислять темы: без него после перезапуска для каждого чата создавалась бы новая тема.
    /// В Docker этот путь нужно вынести на постоянный том.
    /// </summary>
    public string StatePath { get; set; } = "state/topics.json";
}
