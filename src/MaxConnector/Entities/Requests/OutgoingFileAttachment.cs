using System.Text.Json.Serialization;
using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

/// <summary>
/// Вложение «файл» в отправляемом сообщении: загруженный ранее файл по его ID.
/// </summary>
internal sealed record OutgoingFileAttachment(long FileId)
{
    [JsonPropertyName("_type")]
    public string Type => MaxAttachment.File;
}
