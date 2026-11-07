using System.Text.Json.Serialization;
using BridgeTelegramToMax.MaxConnector.Entities.Common;

namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

/// <summary>
/// Вложение «фото» в отправляемом сообщении: загруженное ранее фото по его токену.
/// </summary>
internal sealed record OutgoingPhotoAttachment(string PhotoToken)
{
    [JsonPropertyName("_type")]
    public string Type => MaxAttachment.Photo;
}
