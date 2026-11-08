using System.ComponentModel.DataAnnotations;

namespace BridgeTelegramToMax.Configuration;

public class MaxConfig
{
    public const string SectionName = "MaxConfig";

    /// <summary>
    /// Токен сессии (значение __oneme_auth). Даёт полный доступ к аккаунту.
    /// </summary>
    [Required]
    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Идентификатор устройства (значение __oneme_device_id).
    /// </summary>
    [Required]
    public string DeviceId { get; set; } = string.Empty;

    public string WebSocketUrl { get; set; } = "wss://api.oneme.ru/websocket";

    /// <summary>
    /// Версия веб-клиента.
    /// </summary>
    public string AppVersion { get; set; } = "26.11.3";

    public string UserAgent { get; set; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/147.0.0.0 Safari/537.36";

    [Range(1, 300)]
    public int PingIntervalSeconds { get; set; } = 30;

    [Range(1, 300)]
    public int ReconnectDelaySeconds { get; set; } = 5;

    /// <summary>
    /// Сколько последних сообщений (из всех чатов вместе) выдать при первом входе после запуска приложения.
    /// 0: не загружать. Курсор хранится только в памяти, поэтому после каждого запуска эти сообщения
    /// приходят снова.
    /// </summary>
    [Range(0, 100)]
    public int InitialMessageCount { get; set; } = 20;

    /// <summary>
    /// Сколько секунд ждать, пока сервер MAX обработает загруженный файл, прежде чем отправлять сообщение с ним.
    /// Если уведомление не пришло за это время, файл не отправляется и бот сообщает об ошибке.
    /// </summary>
    [Range(1, 600)]
    public int FileProcessingTimeoutSeconds { get; set; } = 60;
}
