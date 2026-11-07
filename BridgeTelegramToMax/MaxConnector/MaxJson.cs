using System.Text.Json;
using System.Text.Json.Serialization;

namespace BridgeTelegramToMax.MaxConnector;

/// <summary>
/// Настройки System.Text.Json для сообщений протокола: имена полей с маленькой буквы (как в MAX), числа в строках
/// принимаются, лишние поля игнорируются, пустые (null) поля в запросы не попадают.
/// </summary>
internal static class MaxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
