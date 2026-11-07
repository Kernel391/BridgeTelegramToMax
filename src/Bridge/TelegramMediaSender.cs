using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Скачивает вложение MAX и отправляет его в тему Telegram в том же виде: голосовое остаётся голосовым,
/// «кружок» кружком, видео видео, остальное фото или файлом. Если Telegram не принимает файл в нужном виде,
/// пробует отправить его документом, а слишком большой файл заменяет ссылкой: вложение не теряется.
/// </summary>
public sealed class TelegramMediaSender(
    MaxClient maxClient,
    ITelegramBotClient bot,
    IOptions<TelegramConfig> config,
    ILogger<TelegramMediaSender> logger)
{
    private readonly long _groupChatId = config.Value.GroupChatId!.Value;
    private readonly long _maxUploadBytes = config.Value.MaxUploadMegabytes * 1024L * 1024L;

    /// <summary>
    /// True для вложений, которые мы умеем пересылать файлом.
    /// </summary>
    public static bool IsMedia(MaxAttachment attachment)
        => attachment.Type is MaxAttachment.Photo or MaxAttachment.Video or MaxAttachment.Audio or MaxAttachment.File;

    /// <summary>
    /// «Кружок» в Telegram не может иметь подписи, у остальных вложений она есть.
    /// </summary>
    public static bool SupportsCaption(MaxAttachment attachment) => !attachment.IsRoundVideo;

    /// <summary>
    /// Отправляет вложения сообщения MAX в тему Telegram и возвращает все отправленные сообщения Telegram.
    /// Подряд идущие фото и видео (или файлы) собираются в альбомы, остальное идёт по одному. Подпись получает первое
    /// вложение, ответом оформляется первое отправленное сообщение. Вложение, которое не удалось отправить, заменяется
    /// сообщением с причиной: остальные вложения от этого не страдают.
    /// </summary>
    /// <param name="attachments">Вложения вместе с сообщением MAX, в котором они лежат (оно нужно для запроса ссылок).</param>
    /// <param name="firstCaption">Готовый HTML подписи для первого вложения или null.</param>
    /// <param name="replyToTelegramMessageId">Сообщение Telegram, на которое отвечает первое отправленное сообщение, или null.</param>
    public async Task<List<Message>> SendAttachmentsAsync(
        int topicId,
        List<(MaxMessage SourceMessage, MaxAttachment Attachment)> attachments,
        string? firstCaption,
        int? replyToTelegramMessageId,
        CancellationToken cancellationToken)
    {
        var sentMessages = new List<Message>();
        var caption = firstCaption;
        var replyTo = replyToTelegramMessageId;

        foreach (var group in GroupAttachmentsForAlbums(attachments))
        {
            var attachmentsLeftToSend = group;
            if (group.Count > 1)
            {
                var album = await SendAlbumAsync(topicId, group, caption, replyTo, cancellationToken);
                sentMessages.AddRange(album.SentMessages);
                if (album.SentMessages.Count > 0)
                {
                    replyTo = null;
                }

                if (album.CaptionWasSent)
                {
                    caption = null;
                }

                attachmentsLeftToSend = album.AttachmentsNotInAlbum;
            }

            foreach (var (sourceMessage, attachment) in attachmentsLeftToSend)
            {
                try
                {
                    sentMessages.Add(await SendAsync(topicId, sourceMessage, attachment, caption, replyTo, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException && !IsTargetMissing(ex))
                {
                    logger.LogError(ex, "Не удалось переслать вложение {Type} сообщения {MessageId}", attachment.Type, sourceMessage.Id);
                    sentMessages.Add(await SendNoticeAsync(
                        topicId,
                        caption,
                        replyTo,
                        $"⚠️ Не удалось переслать вложение ({AttachmentLabel(attachment)}): {EscapeHtml(ex.Message)}",
                        null,
                        cancellationToken));
                }

                // Подпись и ответ достаются только первому отправленному сообщению.
                caption = null;
                replyTo = null;
            }
        }

        return sentMessages;
    }

    // Делит вложения на группы, которые можно отправить одним альбомом: подряд идущие фото и видео (вместе), либо
    // подряд идущие файлы, не больше 10 штук. Голосовые и кружки в альбом не входят: каждое идёт отдельно.
    private static List<List<(MaxMessage SourceMessage, MaxAttachment Attachment)>> GroupAttachmentsForAlbums(
        List<(MaxMessage SourceMessage, MaxAttachment Attachment)> attachments)
    {
        const int MaxAlbumSize = 10;
        var groups = new List<List<(MaxMessage SourceMessage, MaxAttachment Attachment)>>();
        string? previousAlbumKind = null;
        foreach (var item in attachments)
        {
            var albumKind = item.Attachment switch
            {
                { Type: MaxAttachment.Photo } or { Type: MaxAttachment.Video, IsRoundVideo: false } => "photos-and-videos",
                { Type: MaxAttachment.File } => "documents",
                _ => null,
            };

            if (albumKind is null || albumKind != previousAlbumKind || groups[^1].Count >= MaxAlbumSize)
            {
                groups.Add([]);
            }

            groups[^1].Add(item);
            previousAlbumKind = albumKind;
        }

        return groups;
    }

    /// <summary>
    /// Короткое название вложения для подписей и цитат («Фото», «Голосовое», «Файл имя»). Для вложений, которые
    /// в Telegram не нужны (кнопки, превью ссылок), возвращает null.
    /// </summary>
    public static string? AttachmentLabel(MaxAttachment attachment) => attachment.Type switch
    {
        MaxAttachment.Photo => "Фото",
        MaxAttachment.Video => attachment.IsRoundVideo ? "Кружок" : "Видео",
        MaxAttachment.Audio => "Голосовое",
        MaxAttachment.File => $"Файл {attachment.FileName}".TrimEnd(),
        "STICKER" => "Стикер",
        "CONTACT" => "Контакт",
        _ => null,
    };

    // Отправляет одно вложение в тему. sourceMessage нужен для запроса ссылки на видео или файл, caption это готовый
    // HTML подписи (или null).
    private Task<Message> SendAsync(
        int topicId,
        MaxMessage sourceMessage,
        MaxAttachment attachment,
        string? caption,
        int? replyTo,
        CancellationToken cancellationToken)
        => attachment.Type switch
        {
            MaxAttachment.Photo => SendPhotoAsync(topicId, attachment, caption, replyTo, cancellationToken),
            MaxAttachment.Audio => SendAudioAsync(topicId, attachment, caption, replyTo, cancellationToken),
            MaxAttachment.Video => SendVideoAsync(topicId, sourceMessage, attachment, caption, replyTo, cancellationToken),
            MaxAttachment.File => SendFileAsync(topicId, sourceMessage, attachment, caption, replyTo, cancellationToken),
            _ => throw new NotSupportedException($"Вложение {attachment.Type} не пересылается файлом"),
        };

    /// <summary>
    /// Результат отправки альбома: отправленные сообщения, вложения, которые в альбом не вошли и должны быть
    /// отправлены по одному, и признак того, что подпись ушла вместе с альбомом.
    /// </summary>
    private sealed record AlbumResult(
        List<Message> SentMessages,
        List<(MaxMessage SourceMessage, MaxAttachment Attachment)> AttachmentsNotInAlbum,
        bool CaptionWasSent);

    /// <summary>
    /// Отправляет несколько вложений одним альбомом (не больше 10 штук одного вида: фото и видео вместе, либо
    /// файлы вместе). Подпись получает первый элемент. Что не удалось или нельзя включить в альбом (слишком большое
    /// видео, ошибка скачивания, альбом отклонён Telegram), возвращается в <see cref="AlbumResult.AttachmentsNotInAlbum"/>
    /// для отправки по одному: ничего не теряется.
    /// </summary>
    private async Task<AlbumResult> SendAlbumAsync(
        int topicId,
        IReadOnlyList<(MaxMessage SourceMessage, MaxAttachment Attachment)> items,
        string? caption,
        int? replyTo,
        CancellationToken cancellationToken)
    {
        var prepared = new List<AlbumEntry>();
        var individualIndexes = new HashSet<int>();
        var temporaryFiles = new List<TemporaryDownload>();
        try
        {
            for (var index = 0; index < items.Count; index++)
            {
                var (sourceMessage, attachment) = items[index];
                try
                {
                    var entry = await PrepareAlbumEntryAsync(index, sourceMessage, attachment, temporaryFiles, cancellationToken);
                    if (entry is null)
                    {
                        individualIndexes.Add(index); // слишком большое или без ссылки: одиночная отправка сама пришлёт ссылку
                    }
                    else
                    {
                        prepared.Add(entry);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Одиночная отправка повторит попытку и сообщит об ошибке, если она не случайная.
                    logger.LogWarning("Не удалось подготовить вложение {Type} для альбома: {Message}", attachment.Type, ex.Message);
                    individualIndexes.Add(index);
                }
            }

            var sent = new List<Message>();
            var captionUsed = false;
            if (prepared.Count >= 2)
            {
                try
                {
                    sent.AddRange(await WithRetryAsync(() => SendMediaGroupAsync(topicId, prepared, caption, replyTo, cancellationToken), cancellationToken));
                    captionUsed = caption is not null;
                    prepared.Clear();
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTargetMissing(ex))
                {
                    logger.LogWarning("Telegram не принял альбом ({Message}), отправляю вложения по одному", ex.Message);
                }
            }

            // Всё, что не ушло альбомом, отправляется по одному в исходном порядке.
            individualIndexes.UnionWith(prepared.Select(entry => entry.Index));
            var individual = individualIndexes.Order().Select(index => items[index]).ToList();
            return new AlbumResult(sent, individual, captionUsed);
        }
        finally
        {
            foreach (var temporaryFile in temporaryFiles)
            {
                temporaryFile.Dispose();
            }
        }
    }

    // Скачивает вложение для альбома. Возвращает null, если оно в альбом не годится (нет ссылки, больше лимита).
    private async Task<AlbumEntry?> PrepareAlbumEntryAsync(
        int index,
        MaxMessage sourceMessage,
        MaxAttachment attachment,
        List<TemporaryDownload> temporaryFiles,
        CancellationToken cancellationToken)
    {
        if (attachment.Type == MaxAttachment.Photo)
        {
            var download = await maxClient.DownloadAsync(
                attachment.DownloadUrl ?? throw new InvalidOperationException("У фото нет ссылки"),
                cancellationToken);
            var photoName = download.ContentType switch
            {
                "image/jpeg" => "photo.jpg",
                "image/png" => "photo.png",
                _ => "photo.webp",
            };
            return new AlbumEntry(index, sourceMessage, attachment, photoName, download.Data, null);
        }

        var url = attachment.Type == MaxAttachment.Video
            ? await maxClient.GetVideoUrlAsync(sourceMessage, attachment, cancellationToken)
            : await maxClient.GetFileUrlAsync(sourceMessage, attachment, cancellationToken);
        if (url is null)
        {
            return null;
        }

        var size = attachment.Size ?? await maxClient.GetDownloadSizeAsync(url, cancellationToken);
        if (size > _maxUploadBytes)
        {
            return null;
        }

        var file = await DownloadToTemporaryFileAsync(url, cancellationToken);
        temporaryFiles.Add(file);
        if (file.Size > _maxUploadBytes)
        {
            return null;
        }

        var fileName = attachment.Type == MaxAttachment.Video ? "video.mp4" : attachment.FileName ?? "file";
        return new AlbumEntry(index, sourceMessage, attachment, fileName, null, file.Path);
    }

    private Task<Message[]> SendMediaGroupAsync(int topicId, List<AlbumEntry> entries, string? caption, int? replyTo, CancellationToken cancellationToken)
        => SendWithStreamsAsync(entries, async streams =>
        {
            var album = new List<IAlbumInputMedia>();
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var file = InputFile.FromStream(streams[index], entry.FileName);
                InputMedia input = entry.Attachment.Type switch
                {
                    MaxAttachment.Photo => new InputMediaPhoto(file),
                    MaxAttachment.Video => CreateVideoInput(file, entry.Attachment),
                    _ => new InputMediaDocument(file),
                };

                // Подпись альбома это подпись его первого элемента.
                if (index == 0 && caption is not null)
                {
                    input.Caption = caption;
                    input.ParseMode = ParseMode.Html;
                }

                album.Add((IAlbumInputMedia)input);
            }

            return await bot.SendMediaGroup(_groupChatId, album, replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId, cancellationToken: cancellationToken);
        });

    // Размеры и длительность в InputMediaVideo не допускают null, поэтому задаём их только когда они известны.
    private static InputMediaVideo CreateVideoInput(InputFile file, MaxAttachment video)
    {
        var input = new InputMediaVideo(file) { SupportsStreaming = true };
        if (video.Duration is { } duration)
        {
            input.Duration = (int)Math.Round(duration.TotalSeconds);
        }

        if (video.Width is { } width && video.Height is { } height)
        {
            input.Width = width;
            input.Height = height;
        }

        return input;
    }

    // Открывает потоки файлов альбома на время вызова: при повторе после ожидания потоки создаются заново.
    private static async Task<Message[]> SendWithStreamsAsync(List<AlbumEntry> entries, Func<List<Stream>, Task<Message[]>> send)
    {
        var streams = new List<Stream>();
        try
        {
            streams.AddRange(entries.Select(entry => entry.Data is { } data ? new MemoryStream(data) : (Stream)File.OpenRead(entry.Path!)));
            return await send(streams);
        }
        finally
        {
            foreach (var stream in streams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    // Вложение, готовое к отправке в альбоме: либо байты в памяти (фото), либо временный файл (видео, файлы).
    private sealed record AlbumEntry(int Index, MaxMessage SourceMessage, MaxAttachment Attachment, string FileName, byte[]? Data, string? Path);

    private async Task<Message> SendPhotoAsync(int topicId, MaxAttachment photo, string? caption, int? replyTo, CancellationToken cancellationToken)
    {
        var download = await maxClient.DownloadAsync(
            photo.DownloadUrl ?? throw new InvalidOperationException("У фото нет ссылки"),
            cancellationToken);
        var fileName = download.ContentType switch
        {
            "image/jpeg" => "photo.jpg",
            "image/png" => "photo.png",
            _ => "photo.webp",
        };

        try
        {
            return await WithRetryAsync(
                () => bot.SendPhoto(
                    _groupChatId,
                    InputFile.FromStream(new MemoryStream(download.Data), fileName),
                    caption,
                    ParseMode.Html,
                    replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                    cancellationToken: cancellationToken),
                cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTargetMissing(ex))
        {
            // Например, Telegram не принял формат или размеры картинки: отправляем как есть, документом.
            logger.LogWarning("Telegram не принял фото ({Message}), отправляю документом", ex.Message);
            return await WithRetryAsync(
                () => bot.SendDocument(
                    _groupChatId,
                    InputFile.FromStream(new MemoryStream(download.Data), fileName),
                    caption,
                    ParseMode.Html,
                    replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                    cancellationToken: cancellationToken),
                cancellationToken);
        }
    }

    private async Task<Message> SendAudioAsync(int topicId, MaxAttachment audio, string? caption, int? replyTo, CancellationToken cancellationToken)
    {
        var download = await maxClient.DownloadAsync(
            audio.DownloadUrl ?? throw new InvalidOperationException("У голосового нет ссылки"),
            cancellationToken);
        var durationSeconds = audio.Duration is { } duration ? (int)Math.Round(duration.TotalSeconds) : (int?)null;

        // Telegram показывает как голосовое только OGG с кодеком Opus: смотрим на заголовок потока.
        var streamHeader = System.Text.Encoding.ASCII.GetString(download.Data, 0, Math.Min(download.Data.Length, 64));
        var isOpusVoice = download.ContentType == "audio/ogg" && streamHeader.Contains("OpusHead");
        if (isOpusVoice)
        {
            try
            {
                return await WithRetryAsync(
                    () => bot.SendVoice(
                        _groupChatId,
                        InputFile.FromStream(new MemoryStream(download.Data), "voice.ogg"),
                        caption,
                        ParseMode.Html,
                        duration: durationSeconds,
                        replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                        cancellationToken: cancellationToken),
                    cancellationToken);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTargetMissing(ex))
            {
                logger.LogWarning("Telegram не принял голосовое ({Message}), отправляю как аудиофайл", ex.Message);
            }
        }

        return await WithRetryAsync(
            () => bot.SendAudio(
                _groupChatId,
                InputFile.FromStream(new MemoryStream(download.Data), audio.FileName ?? "audio.ogg"),
                caption,
                ParseMode.Html,
                duration: durationSeconds,
                replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                cancellationToken: cancellationToken),
            cancellationToken);
    }

    private async Task<Message> SendVideoAsync(
        int topicId,
        MaxMessage sourceMessage,
        MaxAttachment video,
        string? caption,
        int? replyTo,
        CancellationToken cancellationToken)
    {
        // Значение videoType нужно, чтобы проверить, что кружки распознаются верно (см. docs/max-protocol.md).
        logger.LogInformation(
            "Видео: videoType={VideoType}, {Width}x{Height}, кружок={IsRound}",
            video.VideoType,
            video.Width,
            video.Height,
            video.IsRoundVideo);

        var url = await maxClient.GetVideoUrlAsync(sourceMessage, video, cancellationToken);
        var label = video.IsRoundVideo ? "Кружок" : "Видео";
        if (url is null)
        {
            return await SendNoticeAsync(topicId, caption, replyTo, $"🎬 {label}: MAX не выдал ссылку на файл", null, cancellationToken);
        }

        var size = await maxClient.GetDownloadSizeAsync(url, cancellationToken);
        if (size > _maxUploadBytes)
        {
            return await SendTooLargeAsync(topicId, caption, replyTo, label, size.Value, url, cancellationToken);
        }

        using var file = await DownloadToTemporaryFileAsync(url, cancellationToken);
        if (file.Size > _maxUploadBytes)
        {
            return await SendTooLargeAsync(topicId, caption, replyTo, label, file.Size, url, cancellationToken);
        }

        try
        {
            if (video.IsRoundVideo)
            {
                try
                {
                    return await WithRetryAsync(
                        async () =>
                        {
                            await using var stream = File.OpenRead(file.Path);
                            return await bot.SendVideoNote(
                                _groupChatId,
                                InputFile.FromStream(stream, "circle.mp4"),
                                duration: video.Duration is { } duration ? (int)Math.Round(duration.TotalSeconds) : null,
                                length: video.Width,
                                replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                                cancellationToken: cancellationToken);
                        },
                        cancellationToken);
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTargetMissing(ex))
                {
                    // Telegram принимает кружок только квадратным и не длиннее минуты: иначе пойдёт обычным видео.
                    logger.LogWarning("Telegram не принял кружок ({Message}), отправляю обычным видео", ex.Message);
                }
            }

            return await WithRetryAsync(
                async () =>
                {
                    await using var stream = File.OpenRead(file.Path);
                    return await bot.SendVideo(
                        _groupChatId,
                        InputFile.FromStream(stream, "video.mp4"),
                        caption,
                        ParseMode.Html,
                        duration: video.Duration is { } duration ? (int)Math.Round(duration.TotalSeconds) : null,
                        width: video.Width,
                        height: video.Height,
                        supportsStreaming: true,
                        replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                        cancellationToken: cancellationToken);
                },
                cancellationToken);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsTargetMissing(ex))
        {
            logger.LogWarning("Telegram не принял видео ({Message}), отправляю документом", ex.Message);
            return await SendDocumentFromFileAsync(topicId, file.Path, "video.mp4", caption, replyTo, cancellationToken);
        }
    }

    private async Task<Message> SendFileAsync(
        int topicId,
        MaxMessage sourceMessage,
        MaxAttachment file,
        string? caption,
        int? replyTo,
        CancellationToken cancellationToken)
    {
        var fileName = file.FileName ?? "file";
        var url = await maxClient.GetFileUrlAsync(sourceMessage, file, cancellationToken);
        if (url is null)
        {
            return await SendNoticeAsync(topicId, caption, replyTo, $"📎 {EscapeHtml(fileName)}: MAX не выдал ссылку на файл", null, cancellationToken);
        }

        var size = file.Size ?? await maxClient.GetDownloadSizeAsync(url, cancellationToken);
        if (size > _maxUploadBytes)
        {
            return await SendTooLargeAsync(topicId, caption, replyTo, $"Файл {fileName}", size.Value, url, cancellationToken);
        }

        using var download = await DownloadToTemporaryFileAsync(url, cancellationToken);
        if (download.Size > _maxUploadBytes)
        {
            return await SendTooLargeAsync(topicId, caption, replyTo, $"Файл {fileName}", download.Size, url, cancellationToken);
        }

        return await SendDocumentFromFileAsync(topicId, download.Path, fileName, caption, replyTo, cancellationToken);
    }

    private Task<Message> SendDocumentFromFileAsync(
        int topicId,
        string path,
        string fileName,
        string? caption,
        int? replyTo,
        CancellationToken cancellationToken)
        => WithRetryAsync(
            async () =>
            {
                await using var stream = File.OpenRead(path);
                return await bot.SendDocument(
                    _groupChatId,
                    InputFile.FromStream(stream, fileName),
                    caption,
                    ParseMode.Html,
                    replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId,
                    cancellationToken: cancellationToken);
            },
            cancellationToken);

    // Файл больше лимита бота: вместо него отправляем ссылку на файл в MAX.
    private Task<Message> SendTooLargeAsync(
        int topicId,
        string? caption,
        int? replyTo,
        string what,
        long size,
        string url,
        CancellationToken cancellationToken)
    {
        logger.LogWarning("{What} ({SizeMb:F1} МБ) больше лимита {LimitMb} МБ, отправляю ссылку", what, size / 1048576.0, config.Value.MaxUploadMegabytes);
        return SendNoticeAsync(
            topicId,
            caption,
            replyTo,
            $"📦 {EscapeHtml(what)} ({size / 1048576.0:F1} МБ) больше лимита бота ({config.Value.MaxUploadMegabytes} МБ). Ссылка на файл в MAX:",
            url,
            cancellationToken);
    }

    // Текстовое сообщение вместо вложения: подпись (если была), пояснение и, при необходимости, ссылка.
    private Task<Message> SendNoticeAsync(int topicId, string? caption, int? replyTo, string notice, string? url, CancellationToken cancellationToken)
    {
        var text = string.Join("\n\n", new[] { caption, notice, url is null ? null : EscapeHtml(url) }.Where(part => !string.IsNullOrEmpty(part)));
        return WithRetryAsync(
            () => bot.SendMessage(_groupChatId, text, ParseMode.Html, replyParameters: ToReplyParameters(replyTo), messageThreadId: topicId, cancellationToken: cancellationToken),
            cancellationToken);
    }

    // Видео и файлы могут быть большими, поэтому качаем их на диск, а не в память.
    private async Task<TemporaryDownload> DownloadToTemporaryFileAsync(string url, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"max-bridge-{Guid.NewGuid():N}");
        try
        {
            await maxClient.DownloadToFileAsync(url, path, cancellationToken);
            return new TemporaryDownload(path, new FileInfo(path).Length);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    private sealed record TemporaryDownload(string Path, long Size) : IDisposable
    {
        public void Dispose() => File.Delete(Path);
    }

    /// <summary>
    /// Повторяет вызов, если Telegram просит подождать (ограничение частоты запросов).
    /// </summary>
    public static async Task<T> WithRetryAsync<T>(Func<Task<T>> call, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await call();
            }
            catch (ApiRequestException ex) when (ex.Parameters?.RetryAfter is { } retryAfterSeconds && attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(retryAfterSeconds + 1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// True, если тема удалена: её нужно создать заново.
    /// </summary>
    public static bool IsTopicMissing(Exception exception)
        => exception is ApiRequestException { ErrorCode: 400 } apiException
           && apiException.Message.Contains("thread not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True, если сообщение, на которое мы отвечаем, в Telegram не найдено (например, его удалили).
    /// </summary>
    public static bool IsReplyTargetMissing(Exception exception)
        => exception is ApiRequestException { ErrorCode: 400 } apiException
           && (apiException.Message.Contains("replied not found", StringComparison.OrdinalIgnoreCase)
               || apiException.Message.Contains("reply message not found", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True, если ошибка не про сам файл, а про место назначения (тема или сообщение для ответа): запасной
    /// вариант отправки тут не поможет, ошибку нужно передать выше.
    /// </summary>
    public static bool IsTargetMissing(Exception exception) => IsTopicMissing(exception) || IsReplyTargetMissing(exception);

    // Ответ на сообщение Telegram: над текстом появится превью исходного сообщения, по нажатию на него открывается оригинал.
    public static ReplyParameters? ToReplyParameters(int? replyToTelegramMessageId)
        => replyToTelegramMessageId is { } messageId ? new ReplyParameters { MessageId = messageId } : null;

    public static string EscapeHtml(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
