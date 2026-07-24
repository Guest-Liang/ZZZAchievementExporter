using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using ZZZae.Protocol.Capture;

namespace ZZZae.App.Infrastructure;

internal abstract record HookMessage;

internal sealed record HookReadyMessage(
    ulong ParserRva,
    int PatternVersion) : HookMessage;

internal sealed record HookPacketMessage(
    CapturedPacket Packet) : HookMessage;

internal sealed record HookErrorMessage(
    string Error) : HookMessage;

internal sealed class HookPipeServer : IAsyncDisposable
{
    private const int MaximumMessageLength =
        32 * 1024 * 1024;
    private const byte ReadyMessage = 1;
    private const byte PacketMessage = 2;
    private const byte ErrorMessage = 3;

    private readonly NamedPipeServerStream _pipe;

    public HookPipeServer(int processId)
    {
        _pipe = new NamedPipeServerStream(
            $"ZZZae-{processId}",
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
            | PipeOptions.CurrentUserOnly);
    }

    public Task WaitForConnectionAsync(
        CancellationToken cancellationToken)
    {
        return _pipe.WaitForConnectionAsync(
            cancellationToken);
    }

    public async Task<HookMessage> ReadMessageAsync(
        CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[sizeof(int)];
        await _pipe.ReadExactlyAsync(
            lengthBuffer,
            cancellationToken);
        var messageLength =
            BinaryPrimitives.ReadInt32LittleEndian(
                lengthBuffer);
        if (messageLength is < 1 or > MaximumMessageLength)
        {
            throw new InvalidDataException(
                $"Hook 消息长度 {messageLength} 无效。");
        }

        var message = GC.AllocateUninitializedArray<byte>(
            messageLength);
        await _pipe.ReadExactlyAsync(
            message,
            cancellationToken);

        return message[0] switch
        {
            ReadyMessage => ParseReady(message),
            PacketMessage => ParsePacket(message),
            ErrorMessage => ParseError(message),
            _ => throw new InvalidDataException(
                $"Hook 消息类型 {message[0]} 未知。")
        };
    }

    public ValueTask DisposeAsync()
    {
        return _pipe.DisposeAsync();
    }

    private static HookReadyMessage ParseReady(
        ReadOnlySpan<byte> message)
    {
        if (message.Length != 13)
        {
            throw new InvalidDataException(
                "Hook 就绪消息长度无效。");
        }

        return new HookReadyMessage(
            BinaryPrimitives.ReadUInt64LittleEndian(
                message[1..9]),
            BinaryPrimitives.ReadInt32LittleEndian(
                message[9..13]));
    }

    private static HookPacketMessage ParsePacket(
        ReadOnlySpan<byte> message)
    {
        if (message.Length < 11)
        {
            throw new InvalidDataException(
                "Hook 数据包消息过短。");
        }

        var commandId =
            BinaryPrimitives.ReadUInt16LittleEndian(
                message[1..3]);
        var headerLength =
            BinaryPrimitives.ReadInt32LittleEndian(
                message[3..7]);
        var bodyLength =
            BinaryPrimitives.ReadInt32LittleEndian(
                message[7..11]);
        if (headerLength < 0
            || bodyLength < 0
            || (long)headerLength + bodyLength
                != message.Length - 11L)
        {
            throw new InvalidDataException(
                "Hook 数据包中的头部或正文长度无效。");
        }

        var header = message
            .Slice(11, headerLength)
            .ToArray();
        var body = message
            .Slice(11 + headerLength, bodyLength)
            .ToArray();

        return new HookPacketMessage(
            new CapturedPacket
            {
                CommandId = commandId,
                Header = header,
                Body = body,
                CapturedAt = DateTimeOffset.UtcNow
            });
    }

    private static HookErrorMessage ParseError(
        ReadOnlySpan<byte> message)
    {
        if (message.Length < 5)
        {
            throw new InvalidDataException(
                "Hook 错误消息过短。");
        }

        var textLength =
            BinaryPrimitives.ReadInt32LittleEndian(
                message[1..5]);
        if (textLength < 0
            || textLength != message.Length - 5)
        {
            throw new InvalidDataException(
                "Hook 错误消息文本长度无效。");
        }

        return new HookErrorMessage(
            Encoding.UTF8.GetString(
                message[5..]));
    }

}
