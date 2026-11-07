using System.Buffers;
using System.Text.Json;
using MessagePack;

namespace BridgeTelegramToMax.MaxConnector;

/// <summary>
/// Тонкая обвязка над MessagePack-CSharp: чтение msgpack в <see cref="JsonElement"/> (чтобы удобно
/// доставать поля) и запись простых значений. От себя здесь только особенности протокола MAX.
/// </summary>
public static class MsgPack
{
    private const int MaxDepth = 64;

    // MAX присылает ID чатов и сообщений как msgpack ext type 1: внутри лежит обычное msgpack-целое.
    // Мы шлём такие числа обычными целыми (сервер принимает оба вида).
    private const sbyte BigIntExtensionType = 1;

    /// <summary>
    /// Разбирает msgpack в JSON. Ext type 1 становится числом, bin base64-строкой, ключи map строками.
    /// </summary>
    public static JsonElement ReadToJson(ReadOnlyMemory<byte> data)
    {
        try
        {
            var reader = new MessagePackReader(data);
            var jsonBuffer = new ArrayBufferWriter<byte>();
            using (var json = new Utf8JsonWriter(jsonBuffer))
            {
                WriteJsonValue(ref reader, json, 0);
            }

            if (!reader.End)
            {
                throw new FormatException("msgpack: лишние байты после значения");
            }

            using var document = JsonDocument.Parse(jsonBuffer.WrittenMemory);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is MessagePackSerializationException or EndOfStreamException or OverflowException)
        {
            throw new FormatException($"msgpack: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Кодирует любое значение: сначала System.Text.Json превращает его в JSON (имена полей с маленькой буквы,
    /// пустые поля пропускаются), затем JSON записывается как msgpack. Подходят записи запросов, словари, списки,
    /// строки, числа и так далее.
    /// </summary>
    public static byte[] Write(object? value)
    {
        var json = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), MaxJson.Options);

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        WriteJsonElement(ref writer, json, 0);
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteJsonValue(ref MessagePackReader reader, Utf8JsonWriter json, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException("msgpack: слишком глубокая вложенность");
        }

        switch (reader.NextMessagePackType)
        {
            case MessagePackType.Nil:
                reader.ReadNil();
                json.WriteNullValue();
                break;
            case MessagePackType.Boolean:
                json.WriteBooleanValue(reader.ReadBoolean());
                break;
            case MessagePackType.Integer:
                var integer = ReadInteger(ref reader);
                if (integer >= long.MinValue && integer <= long.MaxValue)
                {
                    json.WriteNumberValue((long)integer);
                }
                else
                {
                    json.WriteNumberValue((ulong)integer);
                }

                break;
            case MessagePackType.Float:
                var floatingPoint = reader.ReadDouble();
                if (double.IsFinite(floatingPoint))
                {
                    json.WriteNumberValue(floatingPoint);
                }
                else
                {
                    json.WriteNullValue();
                }

                break;
            case MessagePackType.String:
                json.WriteStringValue(reader.ReadString());
                break;
            case MessagePackType.Binary:
                json.WriteBase64StringValue(reader.ReadBytes()!.Value.ToArray());
                break;
            case MessagePackType.Array:
                var arrayLength = reader.ReadArrayHeader();
                json.WriteStartArray();
                for (var i = 0; i < arrayLength; i++)
                {
                    WriteJsonValue(ref reader, json, depth + 1);
                }

                json.WriteEndArray();
                break;
            case MessagePackType.Map:
                var entryCount = reader.ReadMapHeader();
                json.WriteStartObject();
                for (var i = 0; i < entryCount; i++)
                {
                    // JSON требует строковые ключи: числовые (в том числе ext) превращаем в десятичную строку.
                    string key;
                    switch (reader.NextMessagePackType)
                    {
                        case MessagePackType.String:
                            key = reader.ReadString()!;
                            break;
                        case MessagePackType.Integer:
                            key = ReadInteger(ref reader).ToString();
                            break;
                        case MessagePackType.Extension:
                            var keyExtension = reader.ReadExtensionFormat();
                            if (keyExtension.TypeCode != BigIntExtensionType)
                            {
                                throw new FormatException($"msgpack: ключ map с ext type {keyExtension.TypeCode} не поддерживается");
                            }

                            var keyReader = new MessagePackReader(keyExtension.Data);
                            key = ReadInteger(ref keyReader).ToString();
                            if (!keyReader.End)
                            {
                                throw new FormatException("msgpack: лишние байты внутри ext type 1");
                            }

                            break;
                        default:
                            throw new FormatException($"msgpack: ключ map типа {reader.NextMessagePackType} не поддерживается");
                    }

                    json.WritePropertyName(key);
                    WriteJsonValue(ref reader, json, depth + 1);
                }

                json.WriteEndObject();
                break;
            case MessagePackType.Extension:
                var extension = reader.ReadExtensionFormat();
                if (extension.TypeCode == BigIntExtensionType)
                {
                    var extensionReader = new MessagePackReader(extension.Data);
                    var bigInt = ReadInteger(ref extensionReader);
                    if (!extensionReader.End)
                    {
                        throw new FormatException("msgpack: лишние байты внутри ext type 1");
                    }

                    if (bigInt >= long.MinValue && bigInt <= long.MaxValue)
                    {
                        json.WriteNumberValue((long)bigInt);
                    }
                    else
                    {
                        json.WriteNumberValue((ulong)bigInt);
                    }
                }
                else
                {
                    json.WriteStringValue($"ext{extension.TypeCode}:{Convert.ToBase64String(extension.Data.ToArray())}");
                }

                break;
            default:
                throw new FormatException($"msgpack: неподдерживаемый тип {reader.NextMessagePackType}");
        }
    }

    // ReadInt64 понимает все целые, кроме uint64: только он может не поместиться в long.
    private static Int128 ReadInteger(ref MessagePackReader reader)
    {
        if (reader.NextCode == MessagePackCode.UInt64)
        {
            return reader.ReadUInt64();
        }

        return reader.ReadInt64();
    }

    // ---------- JSON -> msgpack ----------

    private static void WriteJsonElement(ref MessagePackWriter writer, JsonElement element, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidOperationException("msgpack: слишком глубокая вложенность");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                writer.Write(true);
                break;
            case JsonValueKind.False:
                writer.Write(false);
                break;
            case JsonValueKind.String:
                writer.Write(element.GetString());
                break;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var signedNumber))
                {
                    writer.Write(signedNumber);
                }
                else if (element.TryGetUInt64(out var unsignedNumber))
                {
                    writer.Write(unsignedNumber);
                }
                else
                {
                    writer.Write(element.GetDouble());
                }

                break;
            case JsonValueKind.Array:
                writer.WriteArrayHeader(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    WriteJsonElement(ref writer, item, depth + 1);
                }

                break;
            case JsonValueKind.Object:
                writer.WriteMapHeader(element.EnumerateObject().Count());
                foreach (var property in element.EnumerateObject())
                {
                    writer.Write(property.Name);
                    WriteJsonElement(ref writer, property.Value, depth + 1);
                }

                break;
            default: // null и «нет значения»
                writer.WriteNil();
                break;
        }
    }
}
