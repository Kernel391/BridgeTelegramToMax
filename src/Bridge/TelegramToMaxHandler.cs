using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace BridgeTelegramToMax.Bridge;

public sealed class TelegramToMaxHandler(
    MaxClient maxClient,
    TopicStore topics,
    IOptions<TelegramConfig> config,
    ILogger<TelegramToMaxHandler> logger)
{
    // Сообщение, написанное давно (пока бот был выключен), в MAX не отправляем: оно было бы неожиданным.
    private static readonly TimeSpan MaxMessageAge = TimeSpan.FromMinutes(2);

    // Альбом Telegram приходит отдельными сообщениями с общим MediaGroupId. Столько ждём остальные вложения альбома,
    // прежде чем отправить их в MAX одним сообщением.
    private static readonly TimeSpan AlbumWaitTime = TimeSpan.FromSeconds(1.5);

    // Столько бот может скачать из Telegram (лимит Bot API).
    private const long MaxDownloadBytes = 20 * 1024 * 1024;

    private readonly long _groupChatId = config.Value.GroupChatId!.Value;

    private readonly Dictionary<string, List<Message>> _pendingAlbums = new();
    private readonly object _pendingAlbumsLock = new();

    /// <summary>
    /// Вложение из сообщения Telegram: что скачать и как назвать файл для MAX. IsPhoto: true для обычного фото
    /// (в MAX уходит фотографией), false для документа (в MAX уходит файлом).
    /// </summary>
    private sealed record TelegramAttachment(string FileId, long? Size, string FileName, string ContentType, bool IsPhoto);

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
        if (message.MessageThreadId is not { } topicId || message.From is not { IsBot: false })
        {
            return;
        }

        if (topics.FindChatByTopic(topicId) is not { } maxChatId)
        {
            return; // тема, которую создали не мы
        }

        var hasAttachment = GetAttachment(message) is not null;
        if (message.Text is not { Length: > 0 } && !hasAttachment)
        {
            await ReplyAsync(bot, message, "В MAX можно отправлять только текст, картинки и файлы.", cancellationToken);
            return;
        }

        if (DateTime.UtcNow - message.Date > MaxMessageAge)
        {
            await ReplyAsync(bot, message, "⏱ Бот был недоступен, сообщение не отправлено в MAX.", cancellationToken);
            return;
        }

        if (message.MediaGroupId is { } albumId && hasAttachment)
        {
            // Первое вложение альбома запускает ожидание остальных, следующие просто добавляются в список.
            bool isFirstInAlbum;
            lock (_pendingAlbumsLock)
            {
                isFirstInAlbum = !_pendingAlbums.TryGetValue(albumId, out var albumMessages);
                if (isFirstInAlbum)
                {
                    albumMessages = [];
                    _pendingAlbums[albumId] = albumMessages;
                }

                albumMessages!.Add(message);
            }

            if (isFirstInAlbum)
            {
                _ = SendAlbumWhenCompleteAsync(bot, albumId, maxChatId, topicId);
            }

            return;
        }

        await SendToMaxAsync(bot, [message], maxChatId, topicId, cancellationToken);
    }

    // Ждёт, пока придут остальные картинки альбома, и отправляет их в MAX одним сообщением. Запускается без ожидания
    // результата (его некому ждать), поэтому все ошибки записывает в журнал сам.
    private async Task SendAlbumWhenCompleteAsync(ITelegramBotClient bot, string albumId, long maxChatId, int topicId)
    {
        try
        {
            await Task.Delay(AlbumWaitTime);

            List<Message> albumMessages;
            lock (_pendingAlbumsLock)
            {
                albumMessages = _pendingAlbums[albumId];
                _pendingAlbums.Remove(albumId);
            }

            await SendToMaxAsync(bot, albumMessages, maxChatId, topicId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Не удалось отправить альбом Telegram {AlbumId} в MAX", albumId);
        }
    }

    // Отправляет в чат MAX одно сообщение, собранное из сообщений Telegram: либо одного текстового, либо одной или
    // нескольких картинок (альбом) с подписью. Результат (успех или причина неудачи) показывает в теме.
    private async Task SendToMaxAsync(
        ITelegramBotClient bot,
        List<Message> telegramMessages,
        long maxChatId,
        int topicId,
        CancellationToken cancellationToken)
    {
        var firstMessage = telegramMessages[0];
        var text = telegramMessages.Select(telegramMessage => telegramMessage.Text ?? telegramMessage.Caption).FirstOrDefault(candidate => !string.IsNullOrEmpty(candidate)) ?? "";

        // В теме у каждого сообщения есть «ответ» на служебное сообщение создания темы, его id равен id темы.
        // Настоящий ответ это ответ на любое другое сообщение.
        long? replyToMaxMessageId = null;
        var isReply = firstMessage.ReplyToMessage is { } repliedMessage && repliedMessage.MessageId != topicId;
        if (isReply)
        {
            replyToMaxMessageId = topics.FindMaxMessage(firstMessage.ReplyToMessage!.MessageId);
        }

        try
        {
            // В одном альбоме Telegram бывают только фото и видео или только документы, поэтому вид сообщения MAX
            // (фотографии или файлы) определяет первое вложение.
            var attachments = telegramMessages.Select(GetAttachment).OfType<TelegramAttachment>().ToList();
            MaxMessage sentMessage;
            if (attachments.Count == 0)
            {
                sentMessage = await maxClient.SendTextAsync(maxChatId, text, replyToMaxMessageId, cancellationToken);
            }
            else
            {
                var downloadedAttachments = new List<(TelegramAttachment Attachment, byte[] Data)>();
                foreach (var attachment in attachments)
                {
                    downloadedAttachments.Add((attachment, await DownloadAttachmentAsync(bot, attachment, cancellationToken)));
                }

                sentMessage = attachments[0].IsPhoto
                    ? await maxClient.SendPhotosAsync(
                        maxChatId,
                        downloadedAttachments.Select(item => new MaxOutgoingPhoto(item.Data, item.Attachment.FileName, item.Attachment.ContentType)).ToList(),
                        text,
                        replyToMaxMessageId,
                        cancellationToken)
                    : await maxClient.SendFilesAsync(
                        maxChatId,
                        downloadedAttachments.Select(item => new MaxOutgoingFile(item.Data, item.Attachment.FileName, item.Attachment.ContentType)).ToList(),
                        text,
                        replyToMaxMessageId,
                        cancellationToken);
            }

            foreach (var telegramMessage in telegramMessages)
            {
                topics.RememberMessage(maxChatId, sentMessage.Id, telegramMessage.MessageId);
            }

            logger.LogInformation("Отправлено в MAX, чат {ChatId}: {Text} (вложений: {AttachmentCount})", maxChatId, text, attachments.Count);

            if (isReply && replyToMaxMessageId is null)
            {
                await ReplyAsync(
                    bot,
                    firstMessage,
                    "Сообщение отправлено без ответа: бот не знает, на какое сообщение MAX оно отвечает (после перезапуска бот забывает старые).",
                    cancellationToken);
            }
            else
            {
                await TelegramMediaSender.WithRetryAsync(
                    async () =>
                    {
                        await bot.SetMessageReaction(
                            firstMessage.Chat.Id,
                            firstMessage.MessageId,
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
            await ReplyAsync(bot, firstMessage, $"❌ Не отправлено в MAX: {reason}", cancellationToken);
        }
    }

    // Вложение сообщения: обычное фото (берём самое большое из присланных размеров, в MAX оно уйдёт фотографией)
    // или документ (любой файл, в MAX он уйдёт файлом). Если вложения нет, возвращает null.
    private static TelegramAttachment? GetAttachment(Message message)
    {
        if (message.Photo is { Length: > 0 } photoSizes)
        {
            var largestSize = photoSizes.MaxBy(photoSize => photoSize.Width * photoSize.Height)!;
            return new TelegramAttachment(largestSize.FileId, largestSize.FileSize, "photo.jpg", "image/jpeg", IsPhoto: true);
        }

        if (message.Document is { } document)
        {
            return new TelegramAttachment(
                document.FileId,
                document.FileSize,
                document.FileName ?? "file",
                document.MimeType ?? "application/octet-stream",
                IsPhoto: false);
        }

        return null;
    }

    private static async Task<byte[]> DownloadAttachmentAsync(ITelegramBotClient bot, TelegramAttachment attachment, CancellationToken cancellationToken)
    {
        var file = await bot.GetFile(attachment.FileId, cancellationToken);
        if (file.FileSize > MaxDownloadBytes)
        {
            throw new InvalidOperationException($"вложение больше {MaxDownloadBytes / 1024 / 1024} МБ, Telegram не отдаёт такие файлы боту");
        }

        using var content = new MemoryStream();
        await bot.DownloadFile(file.FilePath!, content, cancellationToken);
        return content.ToArray();
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
