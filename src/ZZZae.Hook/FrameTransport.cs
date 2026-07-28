using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace ZZZae.Hook;

internal static class FrameTransport
{
    private const int MaximumQueuedBytes = 64 * 1024 * 1024;
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private const byte ReadyMessage = 1;
    private const byte PacketMessage = 2;
    private const byte ErrorMessage = 3;
    private const byte UidMessage = 4;
    private const int UidPollIntervalMilliseconds = 100;
    private const int StableUidObservationCount = 2;

    private static readonly ConcurrentQueue<byte[]> Queue = new();
    private static readonly AutoResetEvent QueueChanged = new(false);

    private static NamedPipeClientStream? _pipe;
    private static int _queuedBytes;
    private static int _shutdown;
    private static int _connected;

    public static void Connect()
    {
        var pipeName = $"ZZZae-{Environment.ProcessId}";
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
        pipe.Connect(30_000);
        _pipe = pipe;
        Volatile.Write(ref _connected, 1);
    }

    public static bool TryEnqueuePacket(ushort commandId, ReadOnlySpan<byte> header, ReadOnlySpan<byte> body)
    {
        if (Volatile.Read(ref _connected) == 0 || Volatile.Read(ref _shutdown) != 0)
        {
            return false;
        }

        var messageLength = checked(
            1 + sizeof(uint) + sizeof(ushort) + sizeof(int) + sizeof(int) + header.Length + body.Length
        );
        if (messageLength > MaximumMessageBytes)
        {
            return false;
        }

        var queued = Interlocked.Add(ref _queuedBytes, messageLength);
        if (queued > MaximumQueuedBytes)
        {
            Interlocked.Add(ref _queuedBytes, -messageLength);
            return false;
        }

        var message = GC.AllocateUninitializedArray<byte>(messageLength);
        var span = message.AsSpan();
        span[0] = PacketMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[5..], commandId);
        BinaryPrimitives.WriteInt32LittleEndian(span[7..], header.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[11..], body.Length);
        header.CopyTo(span[15..]);
        body.CopyTo(span[(15 + header.Length)..]);

        Queue.Enqueue(message);
        QueueChanged.Set();
        return true;
    }

    public static void SendReady(uint parserRva, CurrentUidLocation uidLocation)
    {
        Span<byte> message =
            stackalloc byte[1 + sizeof(ulong) + sizeof(int) + sizeof(ulong) + sizeof(int) + sizeof(int)];
        message[0] = ReadyMessage;
        BinaryPrimitives.WriteUInt64LittleEndian(message[1..], parserRva);
        BinaryPrimitives.WriteInt32LittleEndian(message[9..], PacketHook.LocatorVersion);
        BinaryPrimitives.WriteUInt64LittleEndian(message[13..], uidLocation.RootSlotRva);
        BinaryPrimitives.WriteInt32LittleEndian(message[21..], CurrentUidLocator.LocatorVersion);
        BinaryPrimitives.WriteInt32LittleEndian(message[25..], uidLocation.EquivalentPathCount);
        SendMessage(message);
    }

    public static void Pump(CurrentUidReader uidReader)
    {
        uint candidateUid = 0;
        uint publishedUid = 0;
        var stableObservations = 0;
        long nextUidPoll = 0;

        while (Volatile.Read(ref _shutdown) == 0)
        {
            var now = Environment.TickCount64;
            if (now >= nextUidPoll)
            {
                PollUid(uidReader, ref candidateUid, ref publishedUid, ref stableObservations);
                nextUidPoll = now + UidPollIntervalMilliseconds;
            }

            while (Queue.TryDequeue(out var message))
            {
                Interlocked.Add(ref _queuedBytes, -message.Length);
                if (message.Length >= 1 + sizeof(uint) && message[0] == PacketMessage)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(message.AsSpan(1, sizeof(uint)), publishedUid);
                }

                SendMessage(message);
            }

            QueueChanged.WaitOne(UidPollIntervalMilliseconds);
        }
    }

    public static void RequestShutdown()
    {
        Interlocked.Exchange(ref _shutdown, 1);
        QueueChanged.Set();
    }

    public static void Disconnect()
    {
        Volatile.Write(ref _connected, 0);
        Interlocked.Exchange(ref _pipe, null)?.Dispose();
    }

    public static void TrySendError(string error)
    {
        try
        {
            if (_pipe is null)
            {
                return;
            }

            var encoded = Encoding.UTF8.GetBytes(error);
            var textLength = Math.Min(encoded.Length, 16 * 1024);
            var message = new byte[1 + sizeof(int) + textLength];
            message[0] = ErrorMessage;
            BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(1), textLength);
            encoded.AsSpan(0, textLength).CopyTo(message.AsSpan(5));
            SendMessage(message);
        }
        catch
        {
            // The host may already have closed the pipe.
        }
    }

    private static void PollUid(
        CurrentUidReader uidReader,
        ref uint candidateUid,
        ref uint publishedUid,
        ref int stableObservations
    )
    {
        if (!uidReader.TryRead(out var observedUid))
        {
            candidateUid = 0;
            stableObservations = 0;
            if (publishedUid != 0)
            {
                publishedUid = 0;
                SendUid(0);
            }

            return;
        }

        if (candidateUid == observedUid)
        {
            stableObservations = Math.Min(stableObservations + 1, StableUidObservationCount);
        }
        else
        {
            candidateUid = observedUid;
            stableObservations = 1;
        }

        if (stableObservations >= StableUidObservationCount && publishedUid != candidateUid)
        {
            publishedUid = candidateUid;
            SendUid(publishedUid);
        }
    }

    private static void SendUid(uint uid)
    {
        Span<byte> message = stackalloc byte[1 + sizeof(uint)];
        message[0] = UidMessage;
        BinaryPrimitives.WriteUInt32LittleEndian(message[1..], uid);
        SendMessage(message);
    }

    private static void SendMessage(ReadOnlySpan<byte> message)
    {
        var pipe = _pipe ?? throw new InvalidOperationException("Named pipe is not connected.");

        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, message.Length);
        pipe.Write(length);
        pipe.Write(message);
        pipe.Flush();
    }
}
