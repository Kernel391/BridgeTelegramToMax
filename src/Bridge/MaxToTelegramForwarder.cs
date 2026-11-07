using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static BridgeTelegramToMax.Bridge.TelegramMediaSender;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Пересылает сообщения MAX в Telegram: для каждого чата MAX своя тема группы. Новое сообщение уходит в тему
/// текстом и вложениями, ответ оформляется ответом Telegram (или цитатой, если оригинал неизвестен), правка
/// правит пересланное сообщение, удаление отмечается ответом на него.
/// </summary>
public sealed class MaxToTelegramForwarder(
    MaxClient maxClient,
    ITelegramBotClient bot,
    TelegramMediaSender telegramMediaSender,
    TopicStore topicStore,
    IOptions<TelegramConfig> config,
    ILogger<MaxToTelegramForwarder> logger)
{
    private const int MaxMessageTextLength = 3800;
    private const int MaxCaptionLength = 1024;
    private const int MaxQuotePreviewLength = 300;
    private const int MaxTopicTitleLength = 128;

    private readonly long _groupChatId = config.Value.GroupChatId!.Value;

    private readonly Dictionary<long, MaxChat> _maxChatCache = new();
    private readonly Dictionary<long, string> _maxUserNameCache = new();

    /// <summary>
    /// Пересылает одно сообщение MAX (новое, правку или удаление) в тему его чата.
    /// </summary>
    public async Task ForwardAsync(MaxMessage message, CancellationToken cancellationToken)
    {
        var topic = topicStore.FindByChat(message.ChatId);

        if (message.Status == MaxMessageStatus.Removed)
        {
            await NotifyMessageRemovedAsync(message, topic, cancellationToken);
            return;
        }

        if (message.Status == MaxMessageStatus.Unknown)
        {
            // Статус, которого мы не знаем: считать такое сообщение новым или правкой нельзя.
            logger.LogWarning("Сообщение MAX {MessageId} (чат {ChatId}) с неизвестным статусом не переслано", message.Id, message.ChatId);
            return;
        }

        if (message.IsNew)
        {
            // После перезапуска MAX снова присылает последние сообщения: то, что уже пересылали, пропускаем.
            if (topic is not null && message.Id <= topic.LastForwardedMessageId)
            {
                return;
            }

            // Отправленное из Telegram уже видно в теме, повторять его не нужно.
            if (maxClient.IsSentByThisClient(message))
            {
                if (topic is not null)
                {
                    topic.LastForwardedMessageId = message.Id;
                    topicStore.Save(message.ChatId, topic);
                }

                return;
            }
        }

        var maxChat = await GetMaxChatAsync(message.ChatId, cancellationToken);
        var isOwnMessage = message.SenderId is { } senderId && senderId == maxClient.SelfUserId;
        var senderName = isOwnMessage ? "Я" : await GetMaxUserNameAsync(message.SenderId, cancellationToken);
        var peerName = isOwnMessage ? null : senderName; // в личном чате название темы берётся из имени собеседника

        topic = await EnsureTopicAsync(message.ChatId, topic, maxChat, peerName, cancellationToken);
        try
        {
            await SendToTopicWithReplyFallbackAsync(message, maxChat, senderName, topic.TopicId, cancellationToken);
        }
        catch (Exception ex) when (IsTopicMissing(ex))
        {
            // Тему удалили вручную: создаём новую и отправляем сообщение туда.
            logger.LogWarning("Тема {TopicId} чата MAX {ChatId} удалена, создаю заново", topic.TopicId, message.ChatId);
            topicStore.RemoveChat(message.ChatId);
            topic = await EnsureTopicAsync(message.ChatId, null, maxChat, peerName, cancellationToken);
            await SendToTopicWithReplyFallbackAsync(message, maxChat, senderName, topic.TopicId, cancellationToken);
        }

        if (message.IsNew)
        {
            topic.LastForwardedMessageId = Math.Max(topic.LastForwardedMessageId, message.Id);
            topicStore.Save(message.ChatId, topic);
        }
    }

    // ---------- Темы ----------

    // Возвращает тему чата, создавая её при необходимости. Если тема создана с заглушкой вместо названия, а настоящее
    // название уже известно, переименовывает её.
    private async Task<ChatTopic> EnsureTopicAsync(
        long maxChatId,
        ChatTopic? existingTopic,
        MaxChat? maxChat,
        string? peerName,
        CancellationToken cancellationToken)
    {
        var title = !string.IsNullOrWhiteSpace(maxChat?.Title) ? maxChat.Title
            : !string.IsNullOrWhiteSpace(peerName) ? peerName
            : null;
        var isTitlePlaceholder = title is null;
        title = Truncate(title ?? $"Чат {maxChatId}", MaxTopicTitleLength);

        if (existingTopic is null)
        {
            var forumTopic = await WithRetryAsync(
                () => bot.CreateForumTopic(_groupChatId, title, cancellationToken: cancellationToken),
                cancellationToken);
            var createdTopic = new ChatTopic
            {
                TopicId = forumTopic.MessageThreadId,
                Title = title,
                IsTitlePlaceholder = isTitlePlaceholder,
            };
            topicStore.Save(maxChatId, createdTopic);
            logger.LogInformation("Создана тема «{Title}» ({TopicId}) для чата MAX {ChatId}", title, createdTopic.TopicId, maxChatId);
            return createdTopic;
        }

        if (existingTopic.IsTitlePlaceholder && !isTitlePlaceholder)
        {
            try
            {
                await WithRetryAsync(
                    async () =>
                    {
                        await bot.EditForumTopic(_groupChatId, existingTopic.TopicId, title, cancellationToken: cancellationToken);
                        return true;
                    },
                    cancellationToken);
                existingTopic.Title = title;
                existingTopic.IsTitlePlaceholder = false;
                topicStore.Save(maxChatId, existingTopic);
            }
            catch (ApiRequestException ex) when (!IsTopicMissing(ex))
            {
                // Не получилось переименовать (например, нет права): не страшно, тема продолжит работать.
                logger.LogWarning("Не удалось переименовать тему {TopicId}: {Message}", existingTopic.TopicId, ex.Message);
            }
        }

        return existingTopic;
    }

    // ---------- Отправка ----------

    // Ответ в MAX оформляем ответом Telegram: над текстом превью исходного сообщения (с миниатюрой фото),
    // по нажатию на него открывается оригинал. Так можно только если исходное сообщение мы пересылали и помним
    // (после перезапуска и для старых сообщений такого соответствия нет). Тогда ответ оформляется цитатой текстом.
    private async Task SendToTopicWithReplyFallbackAsync(MaxMessage message, MaxChat? maxChat, string? senderName, int topicId, CancellationToken cancellationToken)
    {
        int? replyToTelegramMessageId = message.Quote is { Kind: MaxQuote.Reply } replyQuote
            ? topicStore.FindTelegramMessage(message.ChatId, replyQuote.Message.Id)
            : null;
        try
        {
            await SendToTopicAsync(message, maxChat, senderName, topicId, replyToTelegramMessageId, cancellationToken);
        }
        catch (Exception ex) when (replyToTelegramMessageId is not null && IsReplyTargetMissing(ex))
        {
            // Исходное сообщение удалили в Telegram: ошибка случилась на первой же отправке, поэтому ничего не продублируется.
            logger.LogWarning(
                "Сообщение Telegram {ReplyToMessageId}, на которое отвечаем, не найдено: оформляю ответ цитатой",
                replyToTelegramMessageId);
            await SendToTopicAsync(message, maxChat, senderName, topicId, null, cancellationToken);
        }
    }

    private async Task SendToTopicAsync(
        MaxMessage message,
        MaxChat? maxChat,
        string? senderName,
        int topicId,
        int? replyToTelegramMessageId,
        CancellationToken cancellationToken)
    {
        var telegramText = await BuildTelegramTextAsync(
            message,
            maxChat,
            senderName,
            replyIsTelegramReply: replyToTelegramMessageId is not null,
            cancellationToken);

        if (message.Status == MaxMessageStatus.Edited)
        {
            await ApplyEditInTelegramAsync(message, telegramText, topicId, cancellationToken);
            return;
        }

        // Пересланное сообщение приходит со своими вложениями: пересылаем их тоже.
        var messagesWithAttachments = message.Quote is { Kind: MaxQuote.Forward } forwardQuote
            ? new[] { message, forwardQuote.Message }
            : new[] { message };
        var attachmentsToSend = messagesWithAttachments
            .SelectMany(sourceMessage => sourceMessage.Attachments
                .Where(IsMedia)
                .Select(attachment => (SourceMessage: sourceMessage, Attachment: attachment)))
            .ToList();

        // Если текст короткий, он идёт подписью к первому вложению. Иначе (или если у вложения не бывает подписи,
        // как у кружка) текст уходит отдельным сообщением перед вложениями.
        string? firstAttachmentCaption = null;
        var textMessages = telegramText.Messages;
        if (attachmentsToSend.Count > 0
            && textMessages.Count == 1
            && telegramText.PlainTextLength <= MaxCaptionLength
            && SupportsCaption(attachmentsToSend[0].Attachment))
        {
            firstAttachmentCaption = textMessages[0];
            textMessages = [];
        }

        // Ответом оформляется только первое отправленное сообщение: текст, а если текста нет, то первое вложение.
        for (var textIndex = 0; textIndex < textMessages.Count; textIndex++)
        {
            var textMessage = textMessages[textIndex];
            var replyParameters = ToReplyParameters(textIndex == 0 ? replyToTelegramMessageId : null);
            var sentMessage = await WithRetryAsync(
                () => bot.SendMessage(_groupChatId, textMessage, ParseMode.Html, replyParameters: replyParameters, messageThreadId: topicId, cancellationToken: cancellationToken),
                cancellationToken);
            topicStore.RememberMessage(message.ChatId, message.Id, sentMessage.MessageId);
        }

        var sentAttachmentMessages = await telegramMediaSender.SendAttachmentsAsync(
            topicId,
            attachmentsToSend,
            firstAttachmentCaption,
            textMessages.Count == 0 ? replyToTelegramMessageId : null,
            cancellationToken);
        foreach (var sentAttachmentMessage in sentAttachmentMessages)
        {
            topicStore.RememberMessage(message.ChatId, message.Id, sentAttachmentMessage.MessageId);
        }
    }

    // Правка в MAX: правим пересланное сообщение. Если оно неизвестно (бот перезапускался) или правка невозможна,
    // присылаем новое сообщение с пометкой.
    private async Task ApplyEditInTelegramAsync(MaxMessage message, TelegramText telegramText, int topicId, CancellationToken cancellationToken)
    {
        var editedText = telegramText.Messages.Count > 0 ? telegramText.Messages[0] : "";
        if (topicStore.FindTelegramMessage(message.ChatId, message.Id) is { } telegramMessageId)
        {
            var editedTextWithMark = $"{editedText}\n<i>(изменено)</i>";
            try
            {
                try
                {
                    await WithRetryAsync(
                        () => bot.EditMessageText(_groupChatId, telegramMessageId, editedTextWithMark, ParseMode.Html, cancellationToken: cancellationToken),
                        cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.Message.Contains("no text in the message", StringComparison.OrdinalIgnoreCase))
                {
                    // В сообщении с вложением текст это подпись.
                    await WithRetryAsync(
                        () => bot.EditMessageCaption(_groupChatId, telegramMessageId, editedTextWithMark, ParseMode.Html, cancellationToken: cancellationToken),
                        cancellationToken);
                }

                return;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTopicMissing(ex))
            {
                logger.LogWarning("Не удалось исправить сообщение Telegram {Id}: {Message}", telegramMessageId, ex.Message);
            }
        }

        var noticeMessage = await WithRetryAsync(
            () => bot.SendMessage(_groupChatId, $"✏️ <i>Сообщение изменено</i>\n{editedText}", ParseMode.Html, messageThreadId: topicId, cancellationToken: cancellationToken),
            cancellationToken);
        topicStore.RememberMessage(message.ChatId, message.Id, noticeMessage.MessageId);
    }

    // Удаление в MAX: отвечаем на пересланное сообщение пометкой. Удалять сообщение из Telegram не будем: содержимое
    // может понадобиться.
    private async Task NotifyMessageRemovedAsync(MaxMessage message, ChatTopic? topic, CancellationToken cancellationToken)
    {
        if (topic is null || topicStore.FindTelegramMessage(message.ChatId, message.Id) is not { } telegramMessageId)
        {
            return;
        }

        try
        {
            await WithRetryAsync(
                () => bot.SendMessage(
                    _groupChatId,
                    "🗑 <i>Сообщение удалено в MAX</i>",
                    ParseMode.Html,
                    replyParameters: new ReplyParameters { MessageId = telegramMessageId },
                    messageThreadId: topic.TopicId,
                    cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400)
        {
            logger.LogWarning("Не удалось отметить удаление сообщения {MessageId}: {Message}", message.Id, ex.Message);
        }
    }

    /// <summary>
    /// Текст сообщения для Telegram: готовые HTML-сообщения (обычно одно) и длина текста без разметки,
    /// по которой проверяются лимиты Telegram.
    /// </summary>
    private sealed record TelegramText(List<string> Messages, int PlainTextLength);

    // Собирает текст: сверху имя отправителя и основной текст, под ним цитата (пересылка, а также ответ, если
    // его нельзя оформить ответом Telegram).
    private async Task<TelegramText> BuildTelegramTextAsync(
        MaxMessage message,
        MaxChat? maxChat,
        string? senderName,
        bool replyIsTelegramReply,
        CancellationToken cancellationToken)
    {
        // В личном чате имя собеседника и так в названии темы, поэтому подписываем только свои сообщения.
        var isOwnMessage = message.SenderId is { } senderId && senderId == maxClient.SelfUserId;
        var senderLabel = isOwnMessage || maxChat?.Type != "DIALOG" ? senderName : null;

        string? quoteHtml = null;
        var quotePlainTextLength = 0;
        // Ответ Telegram уже показывает цитируемое сообщение над текстом, поэтому дублировать его цитатой не нужно.
        if (message.Quote is { } quote && !(replyIsTelegramReply && quote.Kind == MaxQuote.Reply))
        {
            var isQuotedOwnMessage = quote.Message.SenderId is { } quotedSenderId && quotedSenderId == maxClient.SelfUserId;
            var quotedSenderName = isQuotedOwnMessage ? "Я" : await GetMaxUserNameAsync(quote.Message.SenderId, cancellationToken);
            var quotedPreview = Truncate(DescribeForQuote(quote.Message), MaxQuotePreviewLength);
            var quoteHeader = quote.Kind == MaxQuote.Forward
                ? $"↪ Переслано от {quotedSenderName ?? "неизвестного"}{(quote.ChatName is null ? "" : $" ({quote.ChatName})")}"
                : $"↩ {quotedSenderName ?? "Неизвестный"}";
            quoteHtml = $"<blockquote><b>{EscapeHtml(quoteHeader)}</b>\n{EscapeHtml(quotedPreview)}</blockquote>";
            quotePlainTextLength = quoteHeader.Length + quotedPreview.Length + 1;
        }

        // Вложения, которые мы файлом не пересылаем (стикер, контакт), обозначаем строкой в тексте.
        var attachmentPlaceholders = message.Attachments
            .Where(attachment => !IsMedia(attachment))
            .Select(attachment => AttachmentLabel(attachment))
            .Where(attachmentLabel => attachmentLabel is not null)
            .Select(attachmentLabel => $"<i>[{EscapeHtml(attachmentLabel!)}]</i>")
            .ToList();

        var senderLabelHtml = string.IsNullOrEmpty(senderLabel) ? null : $"<b>{EscapeHtml(senderLabel)}</b>";
        var plainTextLength = (senderLabel?.Length ?? 0)
                              + message.Text.Length
                              + quotePlainTextLength
                              + attachmentPlaceholders.Sum(placeholder => placeholder.Length);

        List<string> messages;
        if (plainTextLength <= MaxMessageTextLength)
        {
            messages = [JoinLines([senderLabelHtml, message.Text.Length == 0 ? null : EscapeHtml(message.Text), .. attachmentPlaceholders, quoteHtml])];
        }
        else
        {
            // Очень длинный текст режем на несколько сообщений, цитату отправляем последним.
            messages = SplitText(message.Text, MaxMessageTextLength).Select(EscapeHtml).ToList();
            messages[0] = JoinLines(senderLabelHtml, messages[0]);
            var tail = JoinLines([.. attachmentPlaceholders, quoteHtml]);
            if (tail.Length > 0)
            {
                messages.Add(tail);
            }
        }

        messages.RemoveAll(text => text.Length == 0);
        return new TelegramText(messages, plainTextLength);
    }

    private static string JoinLines(params string?[] lines) => string.Join("\n", lines.Where(line => !string.IsNullOrEmpty(line)));

    // Короткое описание сообщения для цитаты: его текст, а если текста нет, то перечень вложений.
    private static string DescribeForQuote(MaxMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.Text))
        {
            return message.Text;
        }

        return string.Join(", ", message.Attachments.Select(AttachmentLabel).Where(attachmentLabel => attachmentLabel is not null));
    }

    // Режет текст на куски не длиннее chunkSize, стараясь резать по переводу строки или пробелу.
    private static List<string> SplitText(string text, int chunkSize)
    {
        var chunks = new List<string>();
        while (text.Length > chunkSize)
        {
            var cutIndex = text.LastIndexOfAny(['\n', ' '], chunkSize - 1, chunkSize / 2);
            cutIndex = cutIndex > 0 ? cutIndex + 1 : chunkSize;
            chunks.Add(text[..cutIndex]);
            text = text[cutIndex..];
        }

        chunks.Add(text);
        return chunks;
    }

    private static string Truncate(string text, int maxLength) => text.Length <= maxLength ? text : text[..(maxLength - 1)] + "…";

    private async Task<MaxChat?> GetMaxChatAsync(long chatId, CancellationToken cancellationToken)
    {
        if (!_maxChatCache.TryGetValue(chatId, out var maxChat))
        {
            maxChat = await maxClient.GetChatAsync(chatId, cancellationToken);
            if (maxChat is not null)
            {
                _maxChatCache[chatId] = maxChat;
            }
        }

        return maxChat;
    }

    private async Task<string?> GetMaxUserNameAsync(long? userId, CancellationToken cancellationToken)
    {
        if (userId is not { } id)
        {
            return null;
        }

        if (!_maxUserNameCache.TryGetValue(id, out var userName))
        {
            userName = await maxClient.GetUserNameAsync(id, cancellationToken);
            if (userName is not null)
            {
                _maxUserNameCache[id] = userName;
            }
        }

        return userName;
    }
}
