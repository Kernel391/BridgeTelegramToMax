using System.Text.Json;
using BridgeTelegramToMax.Configuration;
using Microsoft.Extensions.Options;

namespace BridgeTelegramToMax.Bridge;

/// <summary>
/// Соответствие чатов MAX и тем Telegram, а также последнее пересланное сообщение каждого чата. Хранится в
/// JSON-файле: без него после перезапуска мост создавал бы новые темы и пересылал бы последние сообщения заново.
/// Кроме того, в памяти (только до перезапуска) хранится соответствие сообщений Telegram и MAX: оно нужно, чтобы
/// ответ в Telegram стал ответом в MAX, а правка в MAX правкой в Telegram.
/// </summary>
public sealed class TopicStore
{
    // Сколько последних пар «сообщение Telegram ↔ сообщение MAX» держим в памяти.
    private const int MaxRememberedMessages = 20_000;

    private readonly string _statePath;
    private readonly ILogger<TopicStore> _logger;
    private readonly object _lock = new();
    private readonly Dictionary<long, ChatTopic> _topicsByChat;

    private readonly Dictionary<int, (long ChatId, long MessageId)> _maxMessageByTelegramMessage = [];
    private readonly Dictionary<(long ChatId, long MessageId), int> _telegramMessageByMaxMessage = [];
    private readonly Queue<int> _rememberedTelegramMessages = new();

    public TopicStore(IOptions<TelegramConfig> config, ILogger<TopicStore> logger)
    {
        _statePath = config.Value.StatePath;
        _logger = logger;
        _topicsByChat = Load();
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
            Save();
        }
    }

    public void RemoveChat(long maxChatId)
    {
        lock (_lock)
        {
            _topicsByChat.Remove(maxChatId);
            Save();
        }
    }

    /// <summary>
    /// Запоминает, что сообщение MAX переслано в Telegram сообщением с таким ID.
    /// </summary>
    public void RememberMessage(long maxChatId, long maxMessageId, int telegramMessageId)
    {
        lock (_lock)
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

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_statePath));
            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            // Пишем во временный файл и подменяем: оборванная запись не испортит рабочий файл.
            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_topicsByChat));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить {Path}: после перезапуска темы Telegram будут созданы заново", _statePath);
        }
    }

    private Dictionary<long, ChatTopic> Load()
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
