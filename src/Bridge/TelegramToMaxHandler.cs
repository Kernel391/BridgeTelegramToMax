using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Отправляет в MAX текст, написанный в теме Telegram: в тот чат MAX, которому принадлежит тема. Ответ на
/// сообщение в Telegram становится ответом в MAX. Успех отмечается реакцией 👍, а неудача ответом с причиной.
/// </summary>
public sealed class TelegramToMaxHandler(
    MaxClient maxClient,
    TopicStore topics,
    IOptions<TelegramConfig> config,
    ILogger<TelegramToMaxHandler> logger)
{
    // Сообщение, написанное давно (пока бот был выключен), в MAX не отправляем: оно было бы неожиданным.
    private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(2);

    private readonly long _groupChatId = config.Value.GroupChatId!.Value;
    
    public async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken cancellationToken)
    {
        if (update.Message is not { } message)
        {
            return;
        }

        if (message.Chat.Id != _groupChatId)
        {
            // Подсказка тем, кто ищет ID группы для настройки.
            logger.LogWarning(
                "Сообщение из чата {ChatId} («{Title}»): мост работает только с группой {GroupChatId}",
                message.Chat.Id,
                message.Chat.Title,
                _groupChatId);
            return;
        }

        // Сообщения вне тем (General) и служебные сообщения о темах нас не интересуют.
        if (message.MessageThreadId is not { } topicId || message.From is not { IsBot: false } sender)
        {
            return;
        }

        if (topics.FindChatByTopic(topicId) is not { } maxChatId)
        {
            return; // тема, которую создали не мы
        }

        if (message.Text is not { Length: > 0 } text)
        {
            await ReplyAsync(bot, message, "В MAX можно отправлять только текст.", cancellationToken);
            return;
        }

        if (DateTime.UtcNow - message.Date > MaxMessageAge)
        {
            await ReplyAsync(bot, message, "⏱ Бот был недоступен, сообщение не отправлено в MAX.", cancellationToken);
            return;
        }

        // В теме у каждого сообщения есть «ответ» на служебное сообщение создания темы, его id равен id темы.
        // Настоящий ответ это ответ на любое другое сообщение.
        long? replyToMaxMessageId = null;
        var isReply = message.ReplyToMessage is { } repliedMessage && repliedMessage.MessageId != topicId;
        if (isReply)
        {
            replyToMaxMessageId = topics.FindMaxMessage(message.ReplyToMessage!.MessageId);
        }

        try
        {
            var sentMessage = await maxClient.SendTextAsync(maxChatId, text, replyToMaxMessageId, cancellationToken);
            topics.RememberMessage(maxChatId, sentMessage.Id, message.MessageId);
            logger.LogInformation("Отправлено в MAX, чат {ChatId}: {Text}", maxChatId, text);

            if (isReply && replyToMaxMessageId is null)
            {
                await ReplyAsync(
                    bot,
                    message,
                    "Сообщение отправлено без ответа: бот не знает, на какое сообщение MAX оно отвечает (после перезапуска бот забывает старые).",
                    cancellationToken);
            }
            else
            {
                await TelegramMediaSender.WithRetryAsync(
                    async () =>
                    {
                        await bot.SetMessageReaction(
                            message.Chat.Id,
                            message.MessageId,
                            [new ReactionTypeEmoji { Emoji = "👍" }],
                            cancellationToken: cancellationToken);
                        return true;
                    },
                    cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Не удалось отправить сообщение в MAX, чат {ChatId}", maxChatId);
            var reason = maxClient.IsConnected ? ex.Message : "нет соединения с MAX";
            await ReplyAsync(bot, message, $"❌ Не отправлено в MAX: {reason}", cancellationToken);
        }
    }

    private async Task ReplyAsync(ITelegramBotClient bot, Message message, string text, CancellationToken cancellationToken)
    {
        try
        {
            await TelegramMediaSender.WithRetryAsync(
                () => bot.SendMessage(
                    message.Chat.Id,
                    text,
                    replyParameters: new ReplyParameters { MessageId = message.MessageId },
                    messageThreadId: message.MessageThreadId,
                    cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Не удалось ответить в Telegram: {Text}", text);
        }
    }
}
