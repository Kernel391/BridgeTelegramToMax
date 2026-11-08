using System.Text.Json;
using BridgeTelegramToMax.Configuration;
using Microsoft.Extensions.Options;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Соответствие чатов MAX и темам Telegram, последнее пересланное сообщение каждого чата и соответствие сообщений
/// MAX и Telegram. Темы и курсоры хранятся в JSON-файле (<see cref="TelegramConfig.StatePath"/>): без него после
/// перезапуска мост создавал бы новые темы и пересылал бы последние сообщения заново. Соответствие сообщений нужно,
/// чтобы ответ в Telegram стал ответом в MAX, правка в MAX правкой в Telegram, а уже виденное сообщение не пересылалось
/// повторно. В памяти оно хранится для последних 20 000 сообщений, а в файле recent-messages.json рядом с файлом
/// состояния сохраняются 100 последних: они переживают перезапуск.
/// </summary>
public sealed class TopicStore
{
    // Сколько последних пар «сообщение Telegram ↔ сообщение MAX» держим в памяти.
    private const int MaxRememberedMessages = 20_000;

    // Сколько последних пар сохраняем в файл, чтобы они пережили перезапуск.
    private const int PersistedMessageCount = 100;

    /// <summary>
    /// Одна пара в файле: сообщение MAX (чат и ID) и сообщение Telegram, в которое оно переслано.
    /// </summary>
    private sealed record RecentMessage(long MaxChatId, long MaxMessageId, int TelegramMessageId);

    private readonly string _statePath;
    private readonly string _recentMessagesPath;
    private readonly ILogger<TopicStore> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, ChatTopic> _topicsByChat;

    private readonly Dictionary<int, (long ChatId, long MessageId)> _maxMessageByTelegramMessage = [];
    private readonly Dictionary<(long ChatId, long MessageId), int> _telegramMessageByMaxMessage = [];
    private readonly Queue<int> _rememberedTelegramMessages = new();

    public TopicStore(IOptions<TelegramConfig> config, ILogger<TopicStore> logger)
    {
        _statePath = config.Value.StatePath;
        _recentMessagesPath = Path.Combine(Path.GetDirectoryName(_statePath) ?? "", "recent-messages.json");
        _logger = logger;
        _topicsByChat = LoadTopics();
        LoadRecentMessages();
    }

    public ChatTopic? FindByChat(long maxChatId)
    {
        lock (_lock)
        {
            return _topicsByChat.GetValueOrDefault(maxChatId);
        }
    }

    public long? FindChatByTopic(int topicId)
    {
        lock (_lock)
        {
            foreach (var (chatId, topic) in _topicsByChat)
            {
                if (topic.TopicId == topicId)
                {
                    return chatId;
                }
            }

            return null;
        }
    }

    public void Save(long maxChatId, ChatTopic topic)
    {
        lock (_lock)
        {
            _topicsByChat[maxChatId] = topic;
            WriteTopicsFile();
        }
    }

    public void RemoveChat(long maxChatId)
    {
        lock (_lock)
        {
            _topicsByChat.Remove(maxChatId);
            WriteTopicsFile();
        }
    }

    /// <summary>
    /// Запоминает, что сообщение MAX связано с сообщением Telegram с таким ID (переслано в него или отправлено из него).
    /// Последние 100 таких пар сразу сохраняются в файл.
    /// </summary>
    public void RememberMessage(long maxChatId, long maxMessageId, int telegramMessageId)
    {
        lock (_lock)
        {
            AddRememberedMessage(maxChatId, maxMessageId, telegramMessageId);
            WriteRecentMessagesFile();
        }
    }

    // Добавляет пару в память, не трогая файл (им же пользуется загрузка из файла).
    private void AddRememberedMessage(long maxChatId, long maxMessageId, int telegramMessageId)
    {
        _maxMessageByTelegramMessage[telegramMessageId] = (maxChatId, maxMessageId);
        // Одному сообщению MAX может соответствовать несколько сообщений Telegram (текст и вложения, альбом).
        // Для правок запоминаем первое: в нём текст или подпись.
        _telegramMessageByMaxMessage.TryAdd((maxChatId, maxMessageId), telegramMessageId);
        _rememberedTelegramMessages.Enqueue(telegramMessageId);

        while (_rememberedTelegramMessages.Count > MaxRememberedMessages)
        {
            var oldTelegramMessageId = _rememberedTelegramMessages.Dequeue();
            if (_maxMessageByTelegramMessage.Remove(oldTelegramMessageId, out var oldMaxMessage)
                && _telegramMessageByMaxMessage.TryGetValue(oldMaxMessage, out var firstTelegramMessageId)
                && firstTelegramMessageId == oldTelegramMessageId)
            {
                _telegramMessageByMaxMessage.Remove(oldMaxMessage);
            }
        }
    }

    public long? FindMaxMessage(int telegramMessageId)
    {
        lock (_lock)
        {
            return _maxMessageByTelegramMessage.TryGetValue(telegramMessageId, out var maxMessage) ? maxMessage.MessageId : null;
        }
    }

    public int? FindTelegramMessage(long maxChatId, long maxMessageId)
    {
        lock (_lock)
        {
            return _telegramMessageByMaxMessage.TryGetValue((maxChatId, maxMessageId), out var telegramMessageId) ? telegramMessageId : null;
        }
    }

    private void WriteTopicsFile()
        => WriteJsonFile(_statePath, _topicsByChat, "после перезапуска темы Telegram будут созданы заново");

    // В файл попадают последние PersistedMessageCount пар в порядке появления (самые новые в конце).
    private void WriteRecentMessagesFile()
    {
        var recentMessages = _rememberedTelegramMessages
            .TakeLast(PersistedMessageCount)
            .Where(_maxMessageByTelegramMessage.ContainsKey)
            .Select(telegramMessageId =>
            {
                var (maxChatId, maxMessageId) = _maxMessageByTelegramMessage[telegramMessageId];
                return new RecentMessage(maxChatId, maxMessageId, telegramMessageId);
            })
            .ToList();
        WriteJsonFile(_recentMessagesPath, recentMessages, "после перезапуска бот не узнает недавние сообщения (правки, ответы и повторы)");
    }

    // Ошибка записи не должна ронять мост: он продолжит работать, но состояние не переживёт перезапуск.
    private void WriteJsonFile(string path, object content, string consequenceOfFailure)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            // Пишем во временный файл и подменяем: оборванная запись не испортит рабочий файл.
            var temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(content));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить {Path}: {Consequence}", path, consequenceOfFailure);
        }
    }

    private void LoadRecentMessages()
    {
        try
        {
            if (!File.Exists(_recentMessagesPath))
            {
                return;
            }

            var recentMessages = JsonSerializer.Deserialize<List<RecentMessage>>(File.ReadAllText(_recentMessagesPath)) ?? [];
            foreach (var recentMessage in recentMessages)
            {
                AddRememberedMessage(recentMessage.MaxChatId, recentMessage.MaxMessageId, recentMessage.TelegramMessageId);
            }

            _logger.LogInformation("Загружено недавних сообщений: {Count} ({Path})", recentMessages.Count, _recentMessagesPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось прочитать {Path}: недавние сообщения не загружены", _recentMessagesPath);
        }
    }

    private Dictionary<long, ChatTopic> LoadTopics()
    {
        try
        {
            if (File.Exists(_statePath))
            {
                var topics = JsonSerializer.Deserialize<Dictionary<long, ChatTopic>>(File.ReadAllText(_statePath)) ?? [];
                _logger.LogInformation("Загружено тем Telegram: {Count} ({Path})", topics.Count, _statePath);
                return topics;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось прочитать {Path}: начинаю с пустого соответствия, темы будут созданы заново", _statePath);
        }

        return [];
    }
}
