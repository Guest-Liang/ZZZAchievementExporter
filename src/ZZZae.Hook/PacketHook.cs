using System.Runtime.InteropServices;

namespace ZZZae.Hook;

internal static unsafe class PacketHook
{
    public const int PatternVersion = 1;

    private const int PatchSize = 16;
    private const int JumpSize = 14;
    private const int MaximumPacketBodyLength = 32 * 1024 * 1024;
    private const uint HeadMagic = 0x0123_4567;
    private const uint TailMagic = 0x89AB_CDEF;

    private static readonly byte[] Pattern =
    [
        0x41,
        0x57,
        0x41,
        0x56,
        0x41,
        0x55,
        0x41,
        0x54,
        0x56,
        0x57,
        0x55,
        0x53,
        0x48,
        0x83,
        0xEC,
        0x48,
        0x45,
        0x89,
        0xCD,
        0x44,
        0x89,
        0xC7,
        0x49,
        0x89,
        0xD4,
    ];

    private static readonly byte[] OriginalBytes = new byte[PatchSize];

    private static nint _target;
    private static nint _trampoline;
    private static int _installed;

    private static delegate* unmanaged<nint, nint, uint, int, byte, int> _original;

    public static ulong WaitForModuleAndInstall(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        nint moduleBase;

        while ((moduleBase = NativeMethods.GetModuleHandle("GameAssembly.dll")) == 0)
        {
            if (FrameTransport.IsShutdownRequested)
            {
                throw new OperationCanceledException("Hook 已请求停止。");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待 GameAssembly.dll 加载超时。");
            }

            Thread.Sleep(25);
        }

        var target = FindUniqueTarget(moduleBase);
        Install(target);
        return checked((ulong)(target - moduleBase));
    }

    public static void Uninstall()
    {
        if (Interlocked.Exchange(ref _installed, 0) == 0 || _target == 0)
        {
            return;
        }

        RestoreOriginalBytes();
    }

    private static void RestoreOriginalBytes()
    {
        if (
            !NativeMethods.VirtualProtect(_target, PatchSize, NativeMethods.PageExecuteReadWrite, out var oldProtection)
        )
        {
            return;
        }

        try
        {
            fixed (byte* source = OriginalBytes)
            {
                Buffer.MemoryCopy(source, (void*)_target, PatchSize, PatchSize);
            }
        }
        finally
        {
            _ = NativeMethods.VirtualProtect(_target, PatchSize, oldProtection, out _);
            _ = NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), _target, PatchSize);
        }
    }

    [UnmanagedCallersOnly]
    private static int Detour(nint parser, nint managedArray, uint offset, int availableLength, byte alternateDecrypt)
    {
        var result = _original(parser, managedArray, offset, availableLength, alternateDecrypt);

        if (result == 1)
        {
            try
            {
                Capture(managedArray, offset, availableLength);
            }
            catch
            {
                // Never let exporter failures escape into the game.
            }
        }

        return result;
    }

    private static void Capture(nint managedArray, uint offset, int availableLength)
    {
        if (managedArray == 0 || availableLength < 16)
        {
            return;
        }

        var arrayLength = *(nuint*)(managedArray + 0x18);
        if ((nuint)offset >= arrayLength)
        {
            return;
        }

        var remainingArrayLength = arrayLength - offset;
        if (remainingArrayLength < 16)
        {
            return;
        }

        var packet = (byte*)managedArray + 0x20 + offset;
        if (ReadBigEndianUInt32(packet) != HeadMagic)
        {
            return;
        }

        var commandId = ReadBigEndianUInt16(packet + 4);
        var headerLength = ReadBigEndianUInt16(packet + 6);
        var bodyLength = ReadBigEndianUInt32(packet + 8);
        if (bodyLength > MaximumPacketBodyLength)
        {
            return;
        }

        var totalLength = 16UL + headerLength + bodyLength;
        if (totalLength > (ulong)availableLength || totalLength > remainingArrayLength)
        {
            return;
        }

        var header = new ReadOnlySpan<byte>(packet + 12, headerLength);
        var body = new ReadOnlySpan<byte>(packet + 12 + headerLength, checked((int)bodyLength));
        var tail = packet + 12 + headerLength + bodyLength;
        if (ReadBigEndianUInt32(tail) != TailMagic)
        {
            return;
        }

        _ = FrameTransport.TryEnqueuePacket(commandId, header, body);
    }

    private static void Install(nint target)
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
        {
            return;
        }

        var originalCaptured = false;
        try
        {
            _target = target;
            var trampolineSize = PatchSize + JumpSize;
            _trampoline = NativeMethods.VirtualAlloc(
                0,
                (nuint)trampolineSize,
                NativeMethods.MemCommit | NativeMethods.MemReserve,
                NativeMethods.PageExecuteReadWrite
            );
            if (_trampoline == 0)
            {
                throw new InvalidOperationException("VirtualAlloc 无法创建 Hook trampoline。");
            }

            fixed (byte* destination = OriginalBytes)
            {
                Buffer.MemoryCopy((void*)target, destination, PatchSize, PatchSize);
            }
            originalCaptured = true;

            Buffer.MemoryCopy((void*)target, (void*)_trampoline, PatchSize, PatchSize);
            WriteAbsoluteJump((byte*)_trampoline + PatchSize, target + PatchSize);

            _original = (delegate* unmanaged<nint, nint, uint, int, byte, int>)_trampoline;

            if (
                !NativeMethods.VirtualProtect(
                    target,
                    PatchSize,
                    NativeMethods.PageExecuteReadWrite,
                    out var oldProtection
                )
            )
            {
                throw new InvalidOperationException("VirtualProtect 无法修改目标函数。");
            }

            try
            {
                WriteAbsoluteJump((byte*)target, (nint)(delegate* unmanaged<nint, nint, uint, int, byte, int>)&Detour);

                for (var index = JumpSize; index < PatchSize; index++)
                {
                    *((byte*)target + index) = 0x90;
                }
            }
            finally
            {
                _ = NativeMethods.VirtualProtect(target, PatchSize, oldProtection, out _);
            }

            if (!NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, PatchSize))
            {
                throw new InvalidOperationException("FlushInstructionCache 失败。");
            }
        }
        catch
        {
            if (originalCaptured)
            {
                RestoreOriginalBytes();
            }

            Interlocked.Exchange(ref _installed, 0);
            throw;
        }
    }

    private static nint FindUniqueTarget(nint moduleBase)
    {
        var image = (byte*)moduleBase;
        if (*(ushort*)image != 0x5A4D)
        {
            throw new InvalidDataException("GameAssembly.dll 不含有效的 DOS 头。");
        }

        var ntOffset = *(int*)(image + 0x3C);
        if (ntOffset <= 0 || *(uint*)(image + ntOffset) != 0x0000_4550)
        {
            throw new InvalidDataException("GameAssembly.dll 不含有效的 PE 头。");
        }

        var fileHeader = image + ntOffset + sizeof(uint);
        var sectionCount = *(ushort*)(fileHeader + 2);
        var optionalHeaderSize = *(ushort*)(fileHeader + 16);
        var optionalHeader = fileHeader + 20;
        var imageSize = *(uint*)(optionalHeader + 56);
        var sectionHeader = optionalHeader + optionalHeaderSize;
        nint found = 0;

        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var section = sectionHeader + sectionIndex * 40;
            var virtualSize = *(uint*)(section + 8);
            var virtualAddress = *(uint*)(section + 12);
            var characteristics = *(uint*)(section + 36);

            if (
                (characteristics & NativeMethods.ImageScnMemExecute) == 0
                || virtualSize < Pattern.Length
                || virtualAddress >= imageSize
            )
            {
                continue;
            }

            var safeSize = Math.Min(virtualSize, imageSize - virtualAddress);
            var start = image + virtualAddress;
            var endOffset = safeSize - (uint)Pattern.Length;

            fixed (byte* pattern = Pattern)
            {
                for (uint offset = 0; offset <= endOffset; offset++)
                {
                    var candidate = start + offset;
                    if (*candidate != *pattern || !Matches(candidate, pattern, Pattern.Length))
                    {
                        continue;
                    }

                    if (found != 0)
                    {
                        throw new InvalidDataException(
                            "明文包解析器特征在 GameAssembly.dll 中出现多次，拒绝安装 Hook。"
                        );
                    }

                    found = (nint)candidate;
                }
            }
        }

        if (found == 0)
        {
            throw new InvalidDataException("未找到 ZZZae 支持的明文包解析器特征。当前游戏版本可能需要更新特征。");
        }

        var functionOffset = (ulong)(found - moduleBase);
        var validationLength = Math.Min(0xC00UL, imageSize - functionOffset);
        if (
            !ContainsUInt32((byte*)found, validationLength, HeadMagic)
            || !ContainsUInt32((byte*)found, validationLength, TailMagic)
        )
        {
            throw new InvalidDataException("特征候选不同时包含包头和包尾魔数，拒绝安装 Hook。");
        }

        return found;
    }

    private static bool Matches(byte* candidate, byte* pattern, int length)
    {
        for (var index = 0; index < length; index++)
        {
            if (candidate[index] != pattern[index])
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsUInt32(byte* start, ulong length, uint value)
    {
        var bytes = (byte*)&value;
        for (ulong offset = 0; offset + sizeof(uint) <= length; offset++)
        {
            if (
                start[offset] == bytes[0]
                && start[offset + 1] == bytes[1]
                && start[offset + 2] == bytes[2]
                && start[offset + 3] == bytes[3]
            )
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteAbsoluteJump(byte* destination, nint target)
    {
        *(ushort*)destination = 0x25FF;
        *(uint*)(destination + 2) = 0;
        *(nint*)(destination + 6) = target;
    }

    private static ushort ReadBigEndianUInt16(byte* value)
    {
        return (ushort)((value[0] << 8) | value[1]);
    }

    private static uint ReadBigEndianUInt32(byte* value)
    {
        return (uint)(value[0] << 24 | value[1] << 16 | value[2] << 8 | value[3]);
    }
}
