namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

/// <summary>
/// Запрос адреса, на который загружается файл (opcode 87). Count: сколько файлов собираемся загрузить.
/// </summary>
internal sealed record GetFileUploadUrlRequest(int Count);
