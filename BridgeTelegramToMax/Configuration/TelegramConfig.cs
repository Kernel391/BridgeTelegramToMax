using System.ComponentModel.DataAnnotations;

namespace BridgeTelegramToMax.Configuration;

public class TelegramConfig
{
    public const string SectionName = "TelegramConfig";

    [Required]
    public string ApiToken { get; set; } = string.Empty;
}
