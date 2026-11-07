using System.Buffers.Binary;
using System.Text.Json;
using K4os.Compression.LZ4;

namespace BridgeTelegramToMax.MaxConnector.Entities;

/// <summary>
/// Один бинарный кадр протокола: заголовок 10 байт (big-endian) и тело в формате MessagePack.
/// Заголовок: версия (1 байт), команда (1), номер запроса (2), opcode (2), флаги сжатия (1), длина тела (3).
/// </summary>
/// <param name="RequestId">Номер запроса: ответ приходит с тем же номером.</param>
/// <param name="Body">Разобранное тело кадра; для пустого тела default (ValueKind == Undefined).</param>
public sealed record MaxFrame(int Version, int Command, int RequestId, int Opcode, JsonElement Body)
{
    /// <summary>
    /// Версия протокола, которую использует веб-клиент.
    /// </summary>
    public const int ProtocolVersion = 10;

    public const int HeaderSize = 10;

    public const int CommandRequest = 0;
    public const int CommandResponse = 1;
    public const int CommandError = 3;

    private const byte ZstdFlag = 0xff;
    private const int MaxUncompressedBodySize = 32 * 1024 * 1024;

    /// <summary>
    /// Собирает кадр без сжатия. Сервер принимает такие кадры, как и веб-клиент для коротких сообщений.
    /// </summary>
    public static byte[] Serialize(int opcode, int requestId, object? body, int command = CommandRequest)
    {
        var bodyBytes = body is null ? [] : MsgPack.Write(body);
        if (bodyBytes.Length > 0xffffff)
        {
            throw new InvalidOperationException("Тело кадра слишком велико");
        }

        var frame = new byte[HeaderSize + bodyBytes.Length];
        frame[0] = ProtocolVersion;
        frame[1] = (byte)command;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2), (ushort)requestId);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)opcode);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(6), (uint)bodyBytes.Length); // флаги сжатия = 0
        bodyBytes.CopyTo(frame, HeaderSize);
        return frame;
    }

    /// <summary>
    /// Разбирает кадр. Бросает FormatException, InvalidDataException или NotSupportedException.
    /// </summary>
    public static MaxFrame Parse(ReadOnlyMemory<byte> data)
    {
        var frameBytes = data.Span;
        if (frameBytes.Length < HeaderSize)
        {
            throw new FormatException($"Кадр MAX короче заголовка: {frameBytes.Length} байт");
        }

        var flagsAndLength = BinaryPrimitives.ReadUInt32BigEndian(frameBytes[6..]);
        var compressionFlags = (byte)(flagsAndLength >> 24);
        var bodyLength = (int)(flagsAndLength & 0xffffff);
        if (HeaderSize + bodyLength != frameBytes.Length)
        {
            throw new FormatException(
                $"Длина в заголовке ({bodyLength}) не сходится с размером кадра ({frameBytes.Length} байт)");
        }

        JsonElement body = default;
        if (bodyLength > 0)
        {
            var storedBody = frameBytes.Slice(HeaderSize, bodyLength);
            if (compressionFlags == ZstdFlag)
            {
                throw new NotSupportedException("Кадр сжат Zstd, такое сжатие не поддерживается");
            }

            byte[]? bodyBytes = null;
            if (compressionFlags == 0)
            {
                bodyBytes = storedBody.ToArray();
            }
            else
            {
                // Флаги 1..254 означают LZ4 block. В кадре нет размера распакованных данных, а библиотека
                // требует буфер заранее: пробуем всё большие буферы. Если не помогло, кадр повреждён.
                for (var bufferSize = Math.Max(storedBody.Length * 4, 256);
                     bufferSize <= MaxUncompressedBodySize && bodyBytes is null;
                     bufferSize *= 4)
                {
                    var buffer = GC.AllocateUninitializedArray<byte>(bufferSize);
                    var decompressedLength = LZ4Codec.Decode(storedBody, buffer);
                    if (decompressedLength >= 0)
                    {
                        bodyBytes = buffer.AsSpan(0, decompressedLength).ToArray();
                    }
                }

                if (bodyBytes is null)
                {
                    throw new InvalidDataException("Не удалось распаковать LZ4: данные повреждены или слишком велики");
                }
            }

            body = MsgPack.ReadToJson(bodyBytes);
        }

        return new MaxFrame(
            Version: frameBytes[0],
            Command: frameBytes[1],
            RequestId: BinaryPrimitives.ReadUInt16BigEndian(frameBytes[2..]),
            Opcode: BinaryPrimitives.ReadUInt16BigEndian(frameBytes[4..]),
            Body: body);
    }
}
