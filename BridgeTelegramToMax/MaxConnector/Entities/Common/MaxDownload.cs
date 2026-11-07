namespace BridgeTelegramToMax.MaxConnector.Entities.Common;

/// <summary>
/// Скачанный файл и его тип, который вернул сервер (например, image/webp).
/// </summary>
public sealed record MaxDownload(byte[] Data, string? ContentType);
