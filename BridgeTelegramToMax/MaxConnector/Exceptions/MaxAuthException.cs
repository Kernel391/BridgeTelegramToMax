namespace BridgeTelegramToMax.MaxConnector.Exceptions;

/// <summary>
/// Сервер отклонил авторизацию (например, токен устарел). Повторять попытки бессмысленно.
/// </summary>
public sealed class MaxAuthException(string message) : Exception(message);
