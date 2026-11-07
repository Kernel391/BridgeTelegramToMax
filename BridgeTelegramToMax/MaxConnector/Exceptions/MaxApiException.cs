namespace BridgeTelegramToMax.MaxConnector.Exceptions;

/// <summary>
/// Сервер ответил на запрос ошибкой или неожиданным ответом.
/// </summary>
public sealed class MaxApiException(string message) : Exception(message);
