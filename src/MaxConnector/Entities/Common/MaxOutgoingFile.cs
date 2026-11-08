namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Файл, который нужно отправить в чат MAX: содержимое, имя и тип (например, application/pdf).
/// </summary>
public sealed record MaxOutgoingFile(byte[] Data, string FileName, string ContentType);
