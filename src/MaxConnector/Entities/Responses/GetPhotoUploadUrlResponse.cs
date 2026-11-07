namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

/// <summary>
/// Ответ на запрос адреса загрузки фото: адрес вида https://iu.oneme.ru/uploadImage?r=...
/// </summary>
internal sealed record GetPhotoUploadUrlResponse(string? Url);
