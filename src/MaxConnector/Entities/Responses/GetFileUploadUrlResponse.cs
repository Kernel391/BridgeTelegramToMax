namespace BridgeTelegramToMax.MaxConnector.Entities.Responses;

/// <summary>
/// Ответ на запрос адреса загрузки файла: по одному элементу на каждый запрошенный файл.
/// </summary>
internal sealed record GetFileUploadUrlResponse(List<FileUploadInfo>? Info);

/// <summary>
/// Куда загружать файл (Url вида https://fu.oneme.ru/api/upload.do?...) и какой ID получит файл в MAX.
/// </summary>
internal sealed record FileUploadInfo(string? Url, long FileId, string? Token);
