using System.Runtime.InteropServices;

namespace ZZZae.Hook;

internal static partial class NativeMethods
{
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint ImageScnMemExecute = 0x2000_0000;

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(
        string moduleName);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetCurrentProcess")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "VirtualAlloc",
        SetLastError = true)]
    internal static partial nint VirtualAlloc(
        nint address,
        nuint size,
        uint allocationType,
        uint protection);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "VirtualProtect",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtection,
        out uint oldProtection);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "FlushInstructionCache",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushInstructionCache(
        nint process,
        nint baseAddress,
        nuint size);
}
