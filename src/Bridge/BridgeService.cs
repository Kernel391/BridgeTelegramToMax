using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using BridgeTelegramToMax.MaxConnector.Exceptions;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Соединяет MAX и Telegram: держит соединение с MAX, пересылает его сообщения в темы группы и принимает
/// сообщения из тем для отправки в MAX. Об обрывах связи и ошибках пишет в тему General.
/// </summary>
public class BridgeService(
    MaxClient maxClient,
    ITelegramBotClient bot,
    MaxToTelegramForwarder forwarder,
    TelegramToMaxHandler telegramHandler,
    GeneralNotifier notifier,
    IOptions<TelegramConfig> telegramConfig,
    IHostApplicationLifetime applicationLifetime,
    ILogger<BridgeService> logger)
    : BackgroundService
{
    private const int ForwardAttempts = 3;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CheckTelegramGroupAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex, "Telegram недоступен или группа настроена неверно. Мост остановлен");
            applicationLifetime.StopApplication();
            return;
        }

        var wasConnected = false;
        maxClient.Connected += () =>
        {
            notifier.Post(wasConnected ? "✅ Соединение с MAX восстановлено" : "✅ Мост запущен, MAX подключён");
            wasConnected = true;
        };
        maxClient.Disconnected += reason =>
            notifier.Post($"⚠️ Соединение с MAX потеряно: {TelegramMediaSender.EscapeHtml(reason)}. Переподключаюсь…");

        bot.StartReceiving(
            updateHandler: telegramHandler.HandleUpdateAsync,
            errorHandler: HandleTelegramErrorAsync,
            receiverOptions: new ReceiverOptions { AllowedUpdates = [UpdateType.Message] },
            cancellationToken: stoppingToken);
        logger.LogInformation("Telegram: приём сообщений из группы запущен");

        // Сообщения MAX читаем в своём цикле: пересылка может сама слать запросы в MAX.
        var notifierTask = notifier.RunAsync(stoppingToken);
        var forwardingTask = ForwardMessagesAsync(stoppingToken);
        try
        {
            await maxClient.RunAsync(stoppingToken);
        }
        catch (MaxAuthException ex)
        {
            logger.LogError(message: ex.Message);
            await notifier.SendAsync(
                $"🛑 MAX отклонил токен: {TelegramMediaSender.EscapeHtml(ex.Message)}\nОбновите MaxConfig:Token и DeviceId и перезапустите мост.",
                CancellationToken.None);

            // Приложение не завершаем: в контейнере с автоперезапуском оно бы запускалось заново и каждый раз писало бы это
            // сообщение в General. Ждём остановки: токен меняют в настройках и перезапускают мост вручную.
            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Остановка приложения.
            }
        }

        await Task.WhenAll(forwardingTask, notifierTask);
    }

    // Бот должен быть в группе с включёнными темами и уметь ими управлять: иначе пересылать некуда.
    private async Task CheckTelegramGroupAsync(CancellationToken cancellationToken)
    {
        var groupChatId = telegramConfig.Value.GroupChatId!.Value;
        var botUser = await bot.GetMe(cancellationToken);
        var group = await bot.GetChat(groupChatId, cancellationToken);
        if (!group.IsForum)
        {
            throw new InvalidOperationException($"В группе «{group.Title}» ({groupChatId}) не включены темы (Topics)");
        }

        var botMember = await bot.GetChatMember(groupChatId, botUser.Id, cancellationToken);
        var canManageTopics = botMember switch
        {
            ChatMemberOwner => true,
            ChatMemberAdministrator administrator => administrator.CanManageTopics == true,
            _ => false,
        };
        if (!canManageTopics)
        {
            throw new InvalidOperationException(
                $"Бот @{botUser.Username} должен быть администратором группы «{group.Title}» с правом «Управление темами»");
        }

        logger.LogInformation("Telegram: бот @{Bot}, группа «{Group}» ({GroupChatId})", botUser.Username, group.Title, groupChatId);
    }

    private async Task ForwardMessagesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in maxClient.Messages.ReadAllAsync(stoppingToken))
            {
                await ForwardWithRetryAsync(message, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка приложения.
        }
    }

    // Пересылает сообщение, при сбое (например, у Telegram или сети) повторяет несколько раз. Если не вышло,
    // сообщает об этом в General и идёт дальше: одно сообщение не должно останавливать остальные.
    private async Task ForwardWithRetryAsync(MaxMessage message, CancellationToken stoppingToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await forwarder.ForwardAsync(message, stoppingToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Не удалось переслать сообщение MAX {MessageId} из чата {ChatId} (попытка {Attempt})", message.Id, message.ChatId, attempt);
                if (attempt >= ForwardAttempts)
                {
                    notifier.Post(
                        $"❌ Не удалось переслать сообщение MAX (чат {message.ChatId}, сообщение {message.Id}): {TelegramMediaSender.EscapeHtml(ex.Message)}");
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), stoppingToken);
            }
        }
    }

    private Task HandleTelegramErrorAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken cancellationToken)
    {
        logger.LogWarning(exception, "Ошибка приёма сообщений Telegram ({Source})", source);
        return Task.CompletedTask;
    }
}
