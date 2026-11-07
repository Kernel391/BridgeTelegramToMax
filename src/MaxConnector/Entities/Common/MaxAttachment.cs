using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Вложение сообщения. Поля заполняются только те, что есть у данного типа вложения. Типы, которые нам не нужны
/// (стикеры, превью ссылок, контакты), приходят с одним только <see cref="Type"/>.
/// </summary>
public sealed record MaxAttachment
{
    public const string Photo = "PHOTO";
    public const string Video = "VIDEO";
    public const string Audio = "AUDIO";
    public const string File = "FILE";

    [JsonPropertyName("_type")]
    public string Type { get; init; } = "UNKNOWN";

    /// <summary>
    /// Фото: ссылка, которая скачивается обычным GET.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// Голосовое: ссылка, которая скачивается обычным GET.
    /// </summary>
    public string? Url { get; init; }

    /// <summary>
    /// Длительность голосового или видео в миллисекундах.
    /// </summary>
    [JsonPropertyName("duration")]
    public long? DurationMs { get; init; }

    /// <summary>
    /// Файл: имя (в протоколе поле name).
    /// </summary>
    [JsonPropertyName("name")]
    public string? FileName { get; init; }

    /// <summary>
    /// Файл: размер в байтах.
    /// </summary>
    public long? Size { get; init; }

    /// <summary>
    /// Видео и файл: токен, нужен для запроса ссылки.
    /// </summary>
    public string? Token { get; init; }

    public long? VideoId { get; init; }

    /// <summary>
    /// Видео: ширина и высота в пикселях.
    /// </summary>
    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>
    /// Видео: тип ролика. Предположительно 1 означает «кружок» (круглое видеосообщение), у обычного видео
    /// поле отсутствует или равно 0. Не проверено на настоящем кружке.
    /// </summary>
    public int? VideoType { get; init; }

    /// <summary>
    /// Видео: true, если это «кружок».
    /// </summary>
    [JsonIgnore]
    public bool IsRoundVideo => Type == Video && VideoType == 1;

    public long? FileId { get; init; }

    /// <summary>
    /// Ссылка для скачивания обычным GET, если она есть прямо во вложении (фото и голосовое).
    /// Для видео и файла ссылку нужно запросить: <see cref="MaxClient.GetVideoUrlAsync"/>,
    /// <see cref="MaxClient.GetFileUrlAsync"/>.
    /// </summary>
    [JsonIgnore]
    public string? DownloadUrl => Type switch
    {
        Photo => BaseUrl,
        Audio => Url,
        _ => null,
    };

    /// <summary>
    /// Длительность голосового или видео.
    /// </summary>
    [JsonIgnore]
    public TimeSpan? Duration => DurationMs is { } milliseconds ? TimeSpan.FromMilliseconds(milliseconds) : null;
}
