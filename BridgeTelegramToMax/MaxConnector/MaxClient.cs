using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using BridgeTelegramToMax.Configuration;
using BridgeTelegramToMax.MaxConnector.Entities;
using BridgeTelegramToMax.MaxConnector.Entities.Common;
using BridgeTelegramToMax.MaxConnector.Entities.Notifications;
using BridgeTelegramToMax.MaxConnector.Entities.Requests;
using BridgeTelegramToMax.MaxConnector.Entities.Responses;
using BridgeTelegramToMax.MaxConnector.Exceptions;
using Microsoft.Extensions.Options;

namespace BridgeTelegramToMax.MaxConnector;

/// <summary>
/// Клиент веб-версии MAX. <see cref="RunAsync"/> держит соединение, входящие сообщения читаются из
/// <see cref="Messages"/>, остальные методы отправляют сообщения и скачивают медиа.
/// Формат протокола описан в docs/max-protocol.md.
/// </summary>
public sealed class MaxClient
{
    private const string Origin = "https://web.max.ru";
    private const int HistoryPageSize = 30;
    private const int MaxReconnectDelaySeconds = 300;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly MaxConfig _config;
    private readonly ILogger<MaxClient> _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly Channel<MaxMessage> _messageQueue = Channel.CreateUnbounded<MaxMessage>();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<MaxFrame>> _pendingRequests = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _webSocket;
    private int _lastRequestId;
    private DateTime _lastFrameReceivedAtUtc = DateTime.UtcNow;
    private int _successfulLoginCount;

    // Идентификатор, который клиент сам придумывает для каждого отправляемого сообщения (cid): время в мс, не повторяется.
    private readonly object _clientMessageIdLock = new();
    private long _lastClientMessageId;

    // Доставка сообщений без дублей. Для каждого чата помним последнее доставленное сообщение: ID и время (мс).
    // Всё хранится только в памяти и защищено _deliveryLock. Предполагается, что ID сообщений в чате растут вместе со временем.
    private readonly Dictionary<long, (long MessageId, long TimeMs)> _lastDeliveredByChat = [];
    private readonly object _deliveryLock = new();
    private readonly long _startedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private List<MaxMessage>? _heldLiveMessages; // живые сообщения, ждущие окончания загрузки пропущенных
    private bool _recentMessagesLoaded;

    public MaxClient(IOptions<MaxConfig> config, ILogger<MaxClient> logger)
    {
        _config = config.Value;
        _logger = logger;

        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(_config.UserAgent);
        _httpClient.DefaultRequestHeaders.Add("Origin", Origin);
    }

    /// <summary>
    /// Входящие сообщения (новые, правки и удаления), в том числе ваши собственные. Читать нужно
    /// отдельным циклом, параллельно с <see cref="RunAsync"/>: так обработчик может сам слать запросы.
    /// </summary>
    public ChannelReader<MaxMessage> Messages => _messageQueue.Reader;

    /// <summary>
    /// ID владельца аккаунта, известен после входа.
    /// </summary>
    public long? SelfUserId { get; private set; }

    /// <summary>
    /// True, пока соединение установлено и вход выполнен.
    /// </summary>
    public bool IsConnected { get; private set; }

    // ---------- Соединение ----------

    /// <summary>
    /// Подключается, принимает сообщения и переподключается при обрыве, пока не отменён токен.
    /// Бросает <see cref="MaxAuthException"/>, если сервер отклонил токен.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consecutiveFailures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var loginCountBefore = _successfulLoginCount;
            try
            {
                await RunSessionAsync(cancellationToken);
                _logger.LogWarning("Соединение с MAX закрыто сервером");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (MaxAuthException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ошибка соединения с MAX");
            }

            // Если до обрыва удалось войти, отсчёт начинается заново. Иначе (сервер недоступен) пауза
            // растёт вдвое с каждой попыткой: 5, 10, 20 ... 300 секунд.
            consecutiveFailures = _successfulLoginCount > loginCountBefore ? 1 : consecutiveFailures + 1;
            var reconnectDelaySeconds = Math.Min(
                _config.ReconnectDelaySeconds * Math.Pow(2, consecutiveFailures - 1),
                MaxReconnectDelaySeconds);
            _logger.LogInformation(
                "Переподключение к MAX через {Seconds} с (попытка {Attempt})",
                reconnectDelaySeconds,
                consecutiveFailures);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnectDelaySeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        using var webSocket = new ClientWebSocket();
        webSocket.Options.SetRequestHeader("Origin", Origin);
        webSocket.Options.SetRequestHeader("User-Agent", _config.UserAgent);
        webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        webSocket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);

        _logger.LogInformation("Подключаюсь к {Url}", _config.WebSocketUrl);
        await webSocket.ConnectAsync(new Uri(_config.WebSocketUrl), cancellationToken);

        using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _webSocket = webSocket;
        _lastFrameReceivedAtUtc = DateTime.UtcNow;
        var receiveTask = ReceiveLoopAsync(webSocket, sessionCancellation.Token);
        var pingTask = Task.CompletedTask;
        try
        {
            await HandshakeAsync(sessionCancellation.Token);
            var loginResponse = await LoginAsync(sessionCancellation.Token);
            _successfulLoginCount++;
            IsConnected = true;

            // Пинг запускаем до загрузки сообщений: она может занять время, а сервер не должен считать нас отключившимся.
            pingTask = PingLoopAsync(webSocket, sessionCancellation.Token);
            await LoadMissedMessagesAsync(loginResponse, sessionCancellation.Token);

            await await Task.WhenAny(receiveTask, pingTask); // пробросить ошибку, если цикл упал
        }
        finally
        {
            IsConnected = false;
            _webSocket = null;
            sessionCancellation.Cancel();
            try
            {
                await Task.WhenAll(receiveTask, pingTask);
            }
            catch
            {
                // Интересует только исходная ошибка.
            }
        }
    }

    private async Task HandshakeAsync(CancellationToken cancellationToken)
    {
        var body = new HandshakeRequest(
            DeviceId: _config.DeviceId,
            UserAgent: new UserAgentInfo(
                DeviceType: "WEB",
                PushDeviceType: "WEBPUSH",
                Locale: "ru",
                DeviceLocale: "ru",
                OsVersion: "Windows",
                DeviceName: "Chrome",
                HeaderUserAgent: _config.UserAgent,
                IsPwa: false,
                AppVersion: _config.AppVersion,
                Screen: "1920x1080 1.0x",
                Timezone: "Europe/Moscow"));

        await SendRequestAsync(MaxOpcode.Handshake, body, cancellationToken);
        _logger.LogInformation("Рукопожатие с MAX выполнено");
    }

    // Возвращает снимок, который MAX присылает при входе (профиль, чаты с последним сообщением и так далее).
    private async Task<LoginResponse> LoginAsync(CancellationToken cancellationToken)
    {
        var body = new LoginRequest(
            Token: _config.Token,
            ChatsCount: 40,
            Interactive: true,
            ChatsSync: -1,
            ContactsSync: -1,
            PresenceSync: -1,
            DraftsSync: -1);

        MaxFrame response;
        try
        {
            response = await SendRequestAsync(MaxOpcode.Login, body, cancellationToken);
        }
        catch (MaxApiException ex)
        {
            throw new MaxAuthException($"{ex.Message}. Скопируйте свежий __oneme_auth из браузера.");
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                "MAX не ответил на вход по токену. Возможно, токен устарел: он мог стать недействительным, "
                + "если вы заходили в web.max.ru с другого устройства. Скопируйте свежий __oneme_auth из браузера.",
                ex);
        }

        // Если снимок не разобрался, работаем без него: просто не будет загрузки пропущенных и своего ID.
        var loginResponse = new LoginResponse();
        try
        {
            loginResponse = response.Body.Deserialize<LoginResponse>(MaxJson.Options) ?? loginResponse;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Не удалось разобрать снимок, который MAX присылает при входе");
        }

        SelfUserId = loginResponse.Profile?.Contact?.Id ?? loginResponse.Profile?.Id;
        _logger.LogInformation("Вход в MAX выполнен. Мой ID: {SelfUserId}", SelfUserId?.ToString() ?? "не определён");
        return loginResponse;
    }

    private async Task PingLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        var body = new PingRequest(Interactive: true);
        var pingTimeout = TimeSpan.FromSeconds(_config.PingIntervalSeconds * 3);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.PingIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var currentTimeout = DateTime.UtcNow - _lastFrameReceivedAtUtc;
            if (currentTimeout > pingTimeout)
            {
                throw new TimeoutException($"MAX ничего не присылает уже {currentTimeout.TotalSeconds:0} с");
            }

            var requestId = Interlocked.Increment(ref _lastRequestId) & 0xffff;
            await SendFrameAsync(webSocket, MaxFrame.Serialize(MaxOpcode.Ping, requestId, body), cancellationToken);
        }
    }

    // ---------- Приём ----------

    private async Task ReceiveLoopAsync(ClientWebSocket webSocket, CancellationToken cancellationToken)
    {
        try
        {
            var receiveBuffer = new byte[16 * 1024];
            using var messageStream = new MemoryStream();

            while (!cancellationToken.IsCancellationRequested)
            {
                messageStream.SetLength(0);
                ValueWebSocketReceiveResult receiveResult;
                do
                {
                    receiveResult = await webSocket.ReceiveAsync(receiveBuffer.AsMemory(), cancellationToken);
                    if (receiveResult.MessageType == WebSocketMessageType.Close)
                    {
                        foreach (var pendingRequest in _pendingRequests.Values)
                        {
                            pendingRequest.TrySetException(new IOException("Соединение закрыто сервером"));
                        }

                        return;
                    }

                    await messageStream.WriteAsync(receiveBuffer, 0, receiveResult.Count, cancellationToken);
                }
                while (!receiveResult.EndOfMessage);

                if (receiveResult.MessageType == WebSocketMessageType.Binary)
                {
                    await HandleIncomingFrameAsync(
                        webSocket,
                        messageStream.GetBuffer().AsMemory(0, (int)messageStream.Length),
                        cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            foreach (var pendingRequest in _pendingRequests.Values)
            {
                pendingRequest.TrySetException(ex);
            }

            throw;
        }
    }

    private async Task HandleIncomingFrameAsync(ClientWebSocket webSocket, ReadOnlyMemory<byte> frameBytes, CancellationToken cancellationToken)
    {
        _lastFrameReceivedAtUtc = DateTime.UtcNow;

        MaxFrame frame;
        try
        {
            frame = MaxFrame.Parse(frameBytes);
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or NotSupportedException or JsonException)
        {
            _logger.LogWarning(ex, "Не удалось разобрать кадр MAX ({Bytes} байт)", frameBytes.Length);
            return;
        }

        _logger.LogDebug(
            "MAX <- opcode={Opcode} command={Command} requestId={RequestId} {Body}",
            frame.Opcode,
            frame.Command,
            frame.RequestId,
            frame.Body);

        // Номера запросов у серверных уведомлений нумеруются отдельно и могут совпасть с нашими,
        // поэтому ответами считаем только кадры с командой «ответ» и «ошибка».
        if (frame.Command is MaxFrame.CommandResponse or MaxFrame.CommandError)
        {
            if (_pendingRequests.TryGetValue(frame.RequestId, out var pendingRequest))
            {
                pendingRequest.TrySetResult(frame);
            }

            return;
        }

        if (frame.Opcode != MaxOpcode.MessageNotification)
        {
            return;
        }

        MessageNotification? notification = null;
        try
        {
            notification = frame.Body.Deserialize<MessageNotification>(MaxJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Не удалось разобрать уведомление о сообщении");
        }

        if (notification?.Message is not { Id: > 0 } notifiedMessage)
        {
            return;
        }

        var message = WithChatId(notifiedMessage, notification.ChatId != 0 ? notification.ChatId : notifiedMessage.ChatId);

        // Веб-клиент подтверждает каждое уведомление: тот же номер запроса, команда «ответ», {chatId, messageId}.
        await SendFrameAsync(
            webSocket,
            MaxFrame.Serialize(
                MaxOpcode.MessageNotification,
                frame.RequestId,
                new MessageNotificationConfirmation(message.ChatId, message.Id),
                MaxFrame.CommandResponse),
            cancellationToken);

        // Пока идёт загрузка пропущенных сообщений, живое придерживаем: иначе оно «перескочит» через ещё не
        // доставленные старые сообщения, и они будут отброшены как дубли.
        lock (_deliveryLock)
        {
            if (_heldLiveMessages is { } heldMessages)
            {
                heldMessages.Add(message);
            }
            else
            {
                PublishMessage(message);
            }
        }
    }

    // В самом сообщении MAX не присылает ID чата (он приходит рядом с сообщением), поэтому проставляем его сами,
    // в том числе у цитируемого сообщения. Цитата внутри цитаты нам не нужна.
    private static MaxMessage WithChatId(MaxMessage message, long chatId)
    {
        var quote = message.Quote;
        if (quote is not null)
        {
            // У ответа chatId в цитате равен 0 или отсутствует: цитируемое сообщение в том же чате.
            var quotedChatId = quote.ChatId is { } quoteChatId and not 0 ? quoteChatId : chatId;
            quote = quote.Message.Id == 0
                ? null
                : quote with { Message = quote.Message with { ChatId = quotedChatId, Quote = null } };
        }

        return message with { ChatId = chatId, Quote = quote };
    }

    // Единственное место, где сообщение попадает в очередь Messages. Новое сообщение пропускается, если в этом
    // чате мы уже доходили до сообщения с таким же или большим ID: так нет дублей, как бы оно ни пришло
    // (живым уведомлением, из истории или повторно после обрыва). Правки и удаления проходят всегда.
    private bool PublishMessage(MaxMessage message)
    {
        lock (_deliveryLock)
        {
            if (message.IsNew)
            {
                if (_lastDeliveredByChat.TryGetValue(message.ChatId, out var lastDelivered) && message.Id <= lastDelivered.MessageId)
                {
                    return false;
                }

                _lastDeliveredByChat[message.ChatId] = (message.Id, message.SentAtMs ?? 0);
            }

            _messageQueue.Writer.TryWrite(message);
            return true;
        }
    }

    // После входа сравниваем последнее сообщение каждого чата из снимка с последним доставленным и подтягиваем
    // недостающее из истории. На время загрузки живые сообщения придерживаются, потом публикуются в порядке прихода.
    private async Task LoadMissedMessagesAsync(LoginResponse loginResponse, CancellationToken cancellationToken)
    {
        lock (_deliveryLock)
        {
            _heldLiveMessages = [];
        }

        try
        {
            // Только при первом входе после запуска: показываем последние сообщения. Дальше по каждому чату
            // действует обычная загрузка пропущенного: чаты, где мы уже дошли до последнего сообщения, она пропустит.
            if (!_recentMessagesLoaded)
            {
                await PublishRecentMessagesAsync(loginResponse.Chats, cancellationToken);
                _recentMessagesLoaded = true;
            }

            foreach (var chat in loginResponse.Chats)
            {
                await LoadMissedMessagesOfChatAsync(chat, cancellationToken);
            }
        }
        catch
        {
            // Связь оборвалась на полпути. Придержанные сообщения не публикуем: иначе мы «перескочим» через ещё не
            // загруженные. Они подтянутся из истории при следующем подключении.
            lock (_deliveryLock)
            {
                _heldLiveMessages = null;
            }

            throw;
        }

        lock (_deliveryLock)
        {
            foreach (var heldMessage in _heldLiveMessages!)
            {
                PublishMessage(heldMessage);
            }

            _heldLiveMessages = null;
        }
    }

    // Публикует InitialMessageCount последних сообщений из всех чатов вместе. Идём по чатам от самого свежего
    // (по последнему сообщению из снимка) и берём из каждого последние сообщения. Останавливаемся, когда
    // следующий чат уже старше самого старого из набранных: в первые N он попасть не сможет.
    private async Task PublishRecentMessagesAsync(List<LoginChat> chats, CancellationToken cancellationToken)
    {
        var messageLimit = _config.InitialMessageCount;
        if (messageLimit <= 0)
        {
            return;
        }

        var chatsNewestFirst = chats
            .Where(chat => chat.LastMessage?.SentAtMs is not null)
            .OrderByDescending(chat => chat.LastMessage!.SentAtMs);

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var recentMessages = new List<MaxMessage>(); // от новых к старым, не больше messageLimit
        foreach (var chat in chatsNewestFirst)
        {
            // Последний элемент recentMessages: самое старое из набранных сообщений.
            if (recentMessages.Count >= messageLimit && chat.LastMessage!.SentAtMs < recentMessages[^1].SentAtMs)
            {
                break;
            }

            try
            {
                // from = сейчас и backward: последние сообщения чата (проверено на настоящем MAX).
                var history = await GetChatHistoryAsync(chat.Id, nowMs, forward: 0, backward: messageLimit, cancellationToken);
                recentMessages.AddRange(history.Where(message => message.Status != "REMOVED"));
            }
            catch (Exception ex) when (ex is MaxApiException or TimeoutException)
            {
                _logger.LogWarning(ex, "Не удалось загрузить последние сообщения чата {ChatId}", chat.Id);
            }

            recentMessages = recentMessages
                .OrderByDescending(message => message.SentAtMs)
                .ThenByDescending(message => message.Id)
                .Take(messageLimit)
                .ToList();
        }

        // Публикуем от старых к новым, как в переписке. Для нас это новые сообщения, даже если их уже правили.
        foreach (var message in recentMessages.OrderBy(message => message.SentAtMs).ThenBy(message => message.Id))
        {
            PublishMessage(message with { Status = null });
        }

        _logger.LogInformation("Загружено последних сообщений: {Count}", recentMessages.Count);
    }

    private async Task LoadMissedMessagesOfChatAsync(LoginChat chat, CancellationToken cancellationToken)
    {
        if (chat.LastMessage is not { Id: > 0, SentAtMs: { } lastMessageTimeMs } lastMessage)
        {
            return;
        }

        // С какого времени запрашивать историю. Запрос включает границу, дубли отсекает PublishMessage.
        long fromTimeMs;
        var newestSeenMessageId = 0L; // новее этого ID мы в истории чата ещё ничего не видели
        lock (_deliveryLock)
        {
            if (_lastDeliveredByChat.TryGetValue(chat.Id, out var lastDelivered))
            {
                if (lastMessage.Id <= lastDelivered.MessageId)
                {
                    return; // ничего нового
                }

                fromTimeMs = lastDelivered.TimeMs;
                newestSeenMessageId = lastDelivered.MessageId;
            }
            else if (lastMessageTimeMs >= _startedAtMs)
            {
                fromTimeMs = _startedAtMs; // чат, который мы ещё не видели: всё, что пришло с запуска приложения
            }
            else
            {
                // Старый чат без новых сообщений: запоминаем, где он закончился.
                _lastDeliveredByChat[chat.Id] = (lastMessage.Id, lastMessageTimeMs);
                return;
            }
        }

        try
        {
            var deliveredCount = 0;
            while (true)
            {
                // Страница ограничена, поэтому повторяем с времени последнего просмотренного сообщения,
                // пока на странице появляется что-то новее уже виденного (удалённые тоже считаются).
                var history = await GetChatHistoryAsync(chat.Id, fromTimeMs, forward: HistoryPageSize, backward: 0, cancellationToken);
                var foundNewerMessage = false;
                foreach (var message in history.OrderBy(message => message.Id))
                {
                    if (message.Id <= newestSeenMessageId)
                    {
                        continue;
                    }

                    newestSeenMessageId = message.Id;
                    fromTimeMs = message.SentAtMs ?? fromTimeMs;
                    foundNewerMessage = true;

                    // Для нас это новое сообщение, даже если его уже успели отредактировать. Удалённые пропускаем.
                    if (message.Status != "REMOVED" && PublishMessage(message with { Status = null }))
                    {
                        deliveredCount++;
                    }
                }

                if (!foundNewerMessage)
                {
                    break;
                }
            }

            if (deliveredCount > 0)
            {
                _logger.LogInformation("Загружено пропущенных сообщений чата {ChatId}: {Count}", chat.Id, deliveredCount);
            }
        }
        catch (Exception ex) when (ex is MaxApiException or TimeoutException)
        {
            _logger.LogWarning(ex, "Не удалось загрузить историю чата {ChatId}, беру только его последнее сообщение", chat.Id);
            PublishMessage(WithChatId(lastMessage, chat.Id) with { Status = null });
        }
    }

    // Сообщения чата вокруг момента fromTimeMs (включительно) в порядке возрастания ID: forward штук после него
    // и backward штук до него.
    private async Task<List<MaxMessage>> GetChatHistoryAsync(
        long chatId,
        long fromTimeMs,
        int forward,
        int backward,
        CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync<GetChatHistoryResponse>(
            MaxOpcode.GetChatHistory,
            new GetChatHistoryRequest(chatId, fromTimeMs, forward, backward, GetMessages: true),
            cancellationToken);

        return response.Messages.Select(message => WithChatId(message, chatId)).ToList();
    }

    // ---------- Отправка ----------

    /// <summary>
    /// Отправляет текстовое сообщение в чат, при необходимости как ответ на сообщение.
    /// </summary>
    /// <param name="replyToMessageId">ID сообщения из этого же чата, на которое нужно ответить.</param>
    public async Task<MaxMessage> SendTextAsync(
        long chatId,
        string text,
        long? replyToMessageId = null,
        CancellationToken cancellationToken = default)
    {
        long clientMessageId;
        lock (_clientMessageIdLock)
        {
            _lastClientMessageId = Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), _lastClientMessageId + 1);
            clientMessageId = _lastClientMessageId;
        }

        var body = new SendMessageRequest(
            ChatId: chatId,
            Notify: true,
            Message: new OutgoingMessage
            {
                ClientMessageId = clientMessageId,
                Text = text,
                Quote = replyToMessageId is { } repliedMessageId ? new OutgoingQuote(MaxQuote.Reply, repliedMessageId) : null,
            });

        // Ответ сервера: ID чата и отправленное сообщение.
        var response = await SendRequestAsync<MessageResponse>(MaxOpcode.SendMessage, body, cancellationToken);
        if (response.Message is not { Id: > 0 } sentMessage)
        {
            throw new MaxApiException("MAX вернул неожиданный ответ на отправку сообщения");
        }

        return WithChatId(sentMessage, response.ChatId != 0 ? response.ChatId : sentMessage.ChatId);
    }

    /// <summary>
    /// Возвращает тип и название чата, либо null, если такого чата нет.
    /// </summary>
    public async Task<MaxChat?> GetChatAsync(long chatId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<GetChatsResponse>(
            MaxOpcode.GetChats,
            new GetChatsRequest([chatId]),
            cancellationToken);

        return response.Chats.FirstOrDefault();
    }

    /// <summary>
    /// Возвращает имя пользователя по его ID, либо null, если пользователь не найден.
    /// </summary>
    public async Task<string?> GetUserNameAsync(long userId, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<GetContactsResponse>(
            MaxOpcode.GetContacts,
            new GetContactsRequest([userId]),
            cancellationToken);

        return response.Contacts.FirstOrDefault()?.Names.FirstOrDefault()?.Name;
    }

    /// <summary>
    /// Возвращает ссылку на видео из вложения (лучшее доступное качество MP4), либо null.
    /// </summary>
    public async Task<string?> GetVideoUrlAsync(MaxMessage message, MaxAttachment video, CancellationToken cancellationToken = default)
    {
        // Ответ вида {"MP4_720": url, "MP4_480": url, ...}: берём максимальное качество.
        var links = await SendRequestAsync<Dictionary<string, JsonElement>>(
            MaxOpcode.GetVideoLinks,
            new GetVideoLinksRequest(message.ChatId, message.Id, video.Token, video.VideoId),
            cancellationToken);

        return links
            .Where(link => link.Key.StartsWith("MP4_") && link.Value.ValueKind == JsonValueKind.String)
            .OrderByDescending(link => int.TryParse(link.Key.AsSpan(4), out var quality) ? quality : 0)
            .Select(link => link.Value.GetString())
            .FirstOrDefault();
    }

    /// <summary>
    /// Возвращает ссылку на файл из вложения FILE, либо null.
    /// </summary>
    public async Task<string?> GetFileUrlAsync(MaxMessage message, MaxAttachment file, CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<GetFileLinkResponse>(
            MaxOpcode.GetFileLink,
            new GetFileLinkRequest(message.ChatId, message.Id, file.FileId),
            cancellationToken);

        return response.Url;
    }

    /// <summary>
    /// Скачивает файл по ссылке из вложения (фото, голосовое, видео, файл) обычным GET.
    /// </summary>
    public async Task<MaxDownload> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new MaxDownload(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType);
    }

    /// <summary>
    /// Отправляет запрос и разбирает ответ в указанный тип. Бросает MaxApiException, если сервер ответил ошибкой
    /// или ответ не удалось разобрать.
    /// </summary>
    private async Task<TResponse> SendRequestAsync<TResponse>(int opcode, object body, CancellationToken cancellationToken)
    {
        var response = await SendRequestAsync(opcode, body, cancellationToken);
        try
        {
            return response.Body.Deserialize<TResponse>(MaxJson.Options)
                   ?? throw new MaxApiException($"MAX вернул пустой ответ на opcode {opcode}");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new MaxApiException($"MAX вернул неожиданный ответ на opcode {opcode}: {ex.Message}");
        }
    }

    /// <summary>
    /// Отправляет запрос и ждёт ответ с тем же номером запроса. Бросает MaxApiException, если сервер ответил ошибкой.
    /// </summary>
    private async Task<MaxFrame> SendRequestAsync(int opcode, object body, CancellationToken cancellationToken)
    {
        var webSocket = _webSocket ?? throw new InvalidOperationException("Нет соединения с MAX");

        // В заголовке номер запроса занимает 16 бит, поэтому нумерация идёт по кругу.
        var requestId = Interlocked.Increment(ref _lastRequestId) & 0xffff;
        var responseCompletion = new TaskCompletionSource<MaxFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[requestId] = responseCompletion;
        try
        {
            await SendFrameAsync(webSocket, MaxFrame.Serialize(opcode, requestId, body), cancellationToken);

            MaxFrame response;
            try
            {
                response = await responseCompletion.Task.WaitAsync(RequestTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"MAX не ответил на запрос opcode {opcode} за {RequestTimeout.TotalSeconds} с");
            }

            // Ошибка приходит как команда «ошибка» либо как поле error в теле ответа ({error, message}).
            string? errorCode = null;
            string? errorMessage = null;
            if (response.Body.ValueKind == JsonValueKind.Object)
            {
                if (response.Body.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String)
                {
                    errorCode = errorElement.GetString();
                }

                // В обычных ответах message это сообщение (объект), а в ошибках это текст.
                if (response.Body.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
                {
                    errorMessage = messageElement.GetString();
                }
            }

            if (response.Command == MaxFrame.CommandError || errorCode is not null)
            {
                throw new MaxApiException($"MAX вернул ошибку на opcode {opcode}: {errorCode} {errorMessage}".Trim());
            }

            return response;
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task SendFrameAsync(ClientWebSocket webSocket, byte[] frameBytes, CancellationToken cancellationToken)
    {
        // ClientWebSocket не поддерживает параллельные отправки.
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await webSocket.SendAsync(frameBytes, WebSocketMessageType.Binary, true, cancellationToken);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
