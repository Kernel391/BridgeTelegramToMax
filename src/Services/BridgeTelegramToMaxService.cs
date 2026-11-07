using BridgeTelegramToMax.Configuration;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BridgeTelegramToMax.Services;

public class BridgeTelegramToMaxService : BackgroundService
{
    private readonly MaxConfig _maxConfig;
    private readonly TelegramConfig _telegramConfig;
    private readonly ILogger<BridgeTelegramToMaxService> _logger;
    private readonly TelegramBotClient _botClient;

    public BridgeTelegramToMaxService(
        IOptions<MaxConfig> maxConfig, 
        IOptions<TelegramConfig> telegramConfig, 
        ILogger<BridgeTelegramToMaxService> logger)
    {
        _maxConfig = maxConfig.Value;
        _telegramConfig = telegramConfig.Value;
        _logger = logger;
        _logger.LogInformation("Стартую службу BridgeTelegramToMaxService");
        _botClient = new TelegramBotClient(_telegramConfig.ApiToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _botClient.StartReceiving(
            updateHandler: HandleUpdateAsync,
            errorHandler: ErrorHandlerAsync,
            receiverOptions: new ReceiverOptions()
            {
                AllowedUpdates = Array.Empty<UpdateType>() // получать все типы обновлений
            },
            cancellationToken: stoppingToken
        );
        _logger.LogInformation("Telegram бот запущен и готов к приему обновлений");
    }

    private async Task ErrorHandlerAsync(ITelegramBotClient client, Exception exception, HandleErrorSource source, CancellationToken token)
    {
        _logger.LogError(exception, "Ошибка при обработке обновления от Telegram");
    }

    private async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken token)
    {
        _logger.LogInformation("Получено обновление от Telegram: {Update}", update);
        // Обрабатываем только текстовые сообщения
        if (update.Message is not { } message) return;
        if (message.Text is not { } messageText) return;

        var chatId = message.Chat.Id;

        _logger.LogInformation("Получено сообщение от чата {ChatId}: {MessageText}", chatId, messageText);

        await client.SendMessage(
            chatId: chatId,
            text: $"Вы написали: {messageText}",
            cancellationToken: token);
    }
}
