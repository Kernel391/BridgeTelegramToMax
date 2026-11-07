namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

/// <summary>
/// Запрос адреса, на который загружается фото (opcode 80). Count: сколько фото собираемся загрузить.
/// </summary>
internal sealed record GetPhotoUploadUrlRequest(int Count);
