using System.Threading.Channels;
using BridgeTelegramToMax.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Пишет служебные сообщения (обрыв связи с MAX, ошибки пересылки) в тему General группы: туда, где нет чатов MAX.
/// Одинаковые сообщения не чаще раза в минуту, чтобы повторяющаяся ошибка не засыпала группу.
/// </summary>
public sealed class GeneralNotifier(ITelegramBotClient bot, IOptions<TelegramConfig> config, ILogger<GeneralNotifier> logger)
{
    private static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(1);

    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    private readonly Dictionary<string, DateTime> _lastSentAtUtc = [];
    private readonly object _lock = new();

    /// <summary>
    /// Ставит сообщение в очередь и сразу возвращает управление: можно вызывать из обработчиков событий.
    /// Текст это HTML Telegram.
    /// </summary>
    public void Post(string html)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (_lastSentAtUtc.TryGetValue(html, out var lastSentAtUtc) && now - lastSentAtUtc < RepeatInterval)
            {
                return;
            }

            _lastSentAtUtc[html] = now;
        }

        _queue.Writer.TryWrite(html);
    }

    /// <summary>
    /// Отправляет сообщения из очереди, пока не отменён токен.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var html in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                await SendAsync(html, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка приложения.
        }
    }

    /// <summary>
    /// Отправляет сообщение сразу, минуя очередь (например, перед остановкой приложения). Ошибки только пишет в лог:
    /// если не работает сам Telegram, сообщить о проблеме всё равно некуда.
    /// </summary>
    public async Task SendAsync(string html, CancellationToken cancellationToken)
    {
        try
        {
            // Без message_thread_id сообщение попадает в тему General.
            await TelegramMediaSender.WithRetryAsync(
                () => bot.SendMessage(config.Value.GroupChatId!.Value, html, ParseMode.Html, cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось отправить служебное сообщение в General: {Text}", html);
        }
    }
}
