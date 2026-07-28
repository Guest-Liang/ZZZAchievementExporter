using System.Runtime.InteropServices;

namespace ZZZae.Hook;

internal readonly record struct PacketHookInstallation(nint ModuleBase, uint ParserRva);

internal static unsafe class PacketHook
{
    /// <summary>
    /// 解析器定位方式的版本。定位规则或被 Hook 函数的语义改变时递增。
    /// </summary>
    public const int LocatorVersion = 2;

    private const int MaxPatchSize = 32;
    private const int JumpSize = 14;
    private const int MaximumPacketBodyLength = 32 * 1024 * 1024;
    private const uint HeadMagic = 0x0123_4567;
    private const uint TailMagic = 0x89AB_CDEF;

    private static readonly byte[] OriginalBytes = new byte[MaxPatchSize];

    private static nint _target;
    private static nint _trampoline;
    private static int _patchSize;
    private static int _installed;

    private static delegate* unmanaged<nint, nint, uint, int, byte, int> _original;

    public static PacketHookInstallation WaitForModuleAndInstall(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        nint moduleBase;

        while ((moduleBase = NativeMethods.GetModuleHandle("GameAssembly.dll")) == 0)
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待 GameAssembly.dll 加载超时。");
            }

            Thread.Sleep(25);
        }

        var location = ParserLocator.Locate(moduleBase, HeadMagic, TailMagic);
        Install(location);
        return new PacketHookInstallation(moduleBase, location.Rva);
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
        var patchSize = (nuint)_patchSize;
        if (
            !NativeMethods.VirtualProtect(_target, patchSize, NativeMethods.PageExecuteReadWrite, out var oldProtection)
        )
        {
            return;
        }

        try
        {
            fixed (byte* source = OriginalBytes)
            {
                Buffer.MemoryCopy(source, (void*)_target, patchSize, patchSize);
            }
        }
        finally
        {
            _ = NativeMethods.VirtualProtect(_target, patchSize, oldProtection, out _);
            _ = NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), _target, patchSize);
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

    private static void Install(ParserLocation location)
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0)
        {
            return;
        }

        var target = location.Address;
        var patchSize = (nuint)location.PatchSize;
        var originalCaptured = false;
        try
        {
            _target = target;
            _patchSize = location.PatchSize;
            var trampolineSize = location.PatchSize + JumpSize;
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
                Buffer.MemoryCopy((void*)target, destination, (nuint)OriginalBytes.Length, patchSize);
            }
            originalCaptured = true;

            Buffer.MemoryCopy((void*)target, (void*)_trampoline, patchSize, patchSize);
            WriteAbsoluteJump((byte*)_trampoline + location.PatchSize, target + location.PatchSize);

            _original = (delegate* unmanaged<nint, nint, uint, int, byte, int>)_trampoline;

            if (
                !NativeMethods.VirtualProtect(
                    target,
                    patchSize,
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

                for (var index = JumpSize; index < location.PatchSize; index++)
                {
                    *((byte*)target + index) = 0x90;
                }
            }
            finally
            {
                _ = NativeMethods.VirtualProtect(target, patchSize, oldProtection, out _);
            }

            if (!NativeMethods.FlushInstructionCache(NativeMethods.GetCurrentProcess(), target, patchSize))
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
