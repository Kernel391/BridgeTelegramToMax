namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Фото, которое нужно отправить в чат MAX: содержимое файла, его имя и тип (например, image/jpeg).
/// </summary>
public sealed record MaxOutgoingPhoto(byte[] Data, string FileName, string ContentType);
