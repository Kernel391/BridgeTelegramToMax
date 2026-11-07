using System.Text;
using BridgeTelegramToMax.MaxConnector;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using BridgeTelegramToMax.MaxConnector.Exceptions;

namespace BridgeTelegramToMax.Services;

/// <summary>
/// Запускает клиент MAX и пока только пишет входящие сообщения в лог. Для фото и голосовых проверяет скачивание,
/// для видео получение ссылки. Позже сюда добавится пересылка в Telegram.
/// </summary>
public class MaxListenerService(MaxClient maxClient, IHostApplicationLifetime applicationLifetime, ILogger<MaxListenerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Сообщения читаем в своём цикле: обработчик может сам слать запросы в MAX.
        var processingTask = ProcessMessagesAsync(stoppingToken);
        try
        {
            await maxClient.RunAsync(stoppingToken);
        }
        catch (MaxAuthException ex)
        {
            logger.LogError("{Message}", ex.Message);
            applicationLifetime.StopApplication();
        }

        await processingTask;
    }

    private async Task ProcessMessagesAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var message in maxClient.Messages.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await HandleMessageAsync(message, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Ошибка при обработке сообщения MAX");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Остановка приложения.
        }
    }

    private async Task HandleMessageAsync(MaxMessage message, CancellationToken cancellationToken)
    {
        if (message.SenderId == maxClient.SelfUserId)
        {
            // Собственные сообщения только отмечаем в журнале: пересылать их не нужно.
            logger.LogInformation(
                "MAX {Status} | своё | чат {ChatId}: {Text} | вложений: {Attachments}",
                message.Status ?? "NEW",
                message.ChatId,
                message.Text,
                string.Join(",", message.Attachments.Select(attachment => attachment.Type)));
            return;
        }

        var chat = await maxClient.GetChatAsync(message.ChatId, cancellationToken);
        var senderName = message.SenderId is { } senderId
            ? await maxClient.GetUserNameAsync(senderId, cancellationToken)
            : null;

        logger.LogInformation(
            "MAX {Status} | {ChatType} {ChatTitle} ({ChatId}) | {SenderName}: {Text} | вложений: {Attachments}",
            message.Status ?? "NEW",
            chat?.Type,
            chat?.Title,
            message.ChatId,
            senderName,
            message.Text,
            string.Join(",", message.Attachments.Select(attachment => attachment.Type)));

        if (message.Quote is { } quote)
        {
            var quotedSenderName = quote.Message.SenderId is { } quotedSenderId
                ? await maxClient.GetUserNameAsync(quotedSenderId, cancellationToken)
                : null;
            logger.LogInformation(
                "  {QuoteKind} {QuotedSenderName}{ForwardedFromChat}: {QuotedText}",
                quote.Kind == MaxQuote.Reply ? "в ответ на" : "переслано от",
                quotedSenderName,
                quote.ChatName is null ? "" : $" ({quote.ChatName})",
                quote.Message.Text);
        }

        foreach (var attachment in message.Attachments)
        {
            // Фото и голосовое скачиваются по ссылке из вложения, у видео и файла ссылку нужно запросить.
            var downloadUrl = attachment.Type switch
            {
                MaxAttachment.Video => await maxClient.GetVideoUrlAsync(message, attachment, cancellationToken),
                MaxAttachment.File => await maxClient.GetFileUrlAsync(message, attachment, cancellationToken),
                _ => attachment.DownloadUrl,
            };

            if (downloadUrl is null)
            {
                // Стикеры, превью ссылок, контакты и прочее нам не нужны.
                logger.LogInformation("Вложение {AttachmentType} пропущено", attachment.Type);
                continue;
            }

            // Видео может быть большим: для проверки его не скачиваем.
            if (attachment.Type == MaxAttachment.Video)
            {
                logger.LogInformation("Видео {Duration}: ссылка получена", attachment.Duration);
                continue;
            }

            var download = await maxClient.DownloadAsync(downloadUrl, cancellationToken);
            logger.LogInformation(
                "{AttachmentType} скачано: {ByteCount} байт, {ContentType}, длительность {Duration}, файл {FileName}",
                attachment.Type,
                download.Data.Length,
                download.ContentType,
                attachment.Duration,
                attachment.FileName);

            // Telegram показывает как голосовое только OGG с кодеком Opus: смотрим на заголовок потока.
            if (download.ContentType == "audio/ogg")
            {
                var streamHeader = Encoding.ASCII.GetString(download.Data, 0, Math.Min(download.Data.Length, 64));
                logger.LogInformation("  Opus: {IsOpus}", streamHeader.Contains("OpusHead"));
            }
        }
    }
}
