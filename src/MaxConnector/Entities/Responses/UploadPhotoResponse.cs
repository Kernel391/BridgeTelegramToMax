namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

/// <summary>
/// Ответ сервера на загрузку фото: {"photos": {"&lt;ключ&gt;": {"token": "..."}}}. Токен нужен, чтобы
/// прикрепить загруженное фото к сообщению.
/// </summary>
internal sealed record UploadPhotoResponse(Dictionary<string, UploadedPhoto>? Photos);

internal sealed record UploadedPhoto(string? Token);
