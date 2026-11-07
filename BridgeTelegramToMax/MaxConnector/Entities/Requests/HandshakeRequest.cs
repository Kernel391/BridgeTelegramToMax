namespace BridgeTelegramToMax.MaxConnector.Entities.Requests;

internal sealed record HandshakeRequest(string DeviceId, UserAgentInfo UserAgent);

internal sealed record UserAgentInfo(
    string DeviceType,
    string PushDeviceType,
    string Locale,
    string DeviceLocale,
    string OsVersion,
    string DeviceName,
    string HeaderUserAgent,
    bool IsPwa,
    string AppVersion,
    string Screen,
    string Timezone);