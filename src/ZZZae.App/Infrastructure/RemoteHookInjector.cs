using System.ComponentModel;
using System.Text;

namespace ZZZae.App.Infrastructure;

internal sealed class RemoteHookSession : IDisposable
{
    private const string StartExport = "ZZZaeHookMain";
    private const string ShutdownExport = "ZZZaeHookShutdown";

    private readonly SuspendedGameProcess _game;
    private readonly string _hookPath;
    private readonly nint _remoteModuleBase;
    private int _shutdown;

    internal RemoteHookSession(SuspendedGameProcess game, string hookPath, nint remoteModuleBase)
    {
        _game = game;
        _hookPath = hookPath;
        _remoteModuleBase = remoteModuleBase;
    }

    public void Start()
    {
        var exitCode = InvokeExport(StartExport);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Hook 初始化入口返回 {exitCode}。");
        }
    }

    public void Dispose()
    {
        try
        {
            Shutdown();
        }
        catch
        {
            // The game may already have exited. Hook-side pipe cleanup
            // also removes the detour when the host disconnects.
        }
    }

    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        var exitCode = InvokeExport(ShutdownExport);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Hook 关闭入口返回 {exitCode}。");
        }
    }

    private uint InvokeExport(string exportName)
    {
        var exportRva = RemoteHookInjector.GetExportRva(_hookPath, exportName);
        var remoteAddress = checked(_remoteModuleBase + exportRva);
        return RemoteHookInjector.RunRemoteThread(
            _game.ProcessHandle,
            remoteAddress,
            0,
            TimeSpan.FromSeconds(30),
            exportName
        );
    }
}

internal static unsafe class RemoteHookInjector
{
    private const string Kernel32Name = "kernel32.dll";
    private const string LoadLibraryName = "LoadLibraryW";

    public static RemoteHookSession Inject(SuspendedGameProcess game, string hookPath)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentException.ThrowIfNullOrWhiteSpace(hookPath);

        var fullHookPath = Path.GetFullPath(hookPath);
        var localKernel32 = NativeMethods.GetModuleHandle(Kernel32Name);
        if (localKernel32 == 0)
        {
            throw SuspendedGameProcess.NewWin32Exception("读取本机 kernel32.dll 句柄失败");
        }

        var localLoadLibrary = NativeMethods.GetProcAddress(localKernel32, LoadLibraryName);
        if (localLoadLibrary == 0)
        {
            throw SuspendedGameProcess.NewWin32Exception("解析 LoadLibraryW 地址失败");
        }

        if (
            !NativeMethods.GetModuleHandleEx(
                NativeMethods.GetModuleHandleExFromAddress | NativeMethods.GetModuleHandleExUnchangedRefCount,
                localLoadLibrary,
                out var localProcedureModule
            )
            || localProcedureModule == 0
        )
        {
            throw SuspendedGameProcess.NewWin32Exception("解析 LoadLibraryW 所属模块失败");
        }

        var procedureModuleName = ReadLocalModuleName(localProcedureModule);
        var remoteProcedureModule = FindRemoteModuleBase(game.ProcessId, procedureModuleName, TimeSpan.FromSeconds(10));
        var loadLibraryRva = checked(localLoadLibrary - localProcedureModule);
        var remoteLoadLibrary = checked(remoteProcedureModule + loadLibraryRva);

        LoadRemoteLibrary(game.ProcessHandle, remoteLoadLibrary, fullHookPath);

        var remoteHook = FindRemoteModuleBase(game.ProcessId, Path.GetFileName(fullHookPath), TimeSpan.FromSeconds(10));
        var session = new RemoteHookSession(game, fullHookPath, remoteHook);
        session.Start();
        return session;
    }

    internal static nint GetExportRva(string modulePath, string exportName)
    {
        var module = NativeMethods.LoadLibraryEx(modulePath, 0, NativeMethods.DontResolveDllReferences);
        if (module == 0)
        {
            throw SuspendedGameProcess.NewWin32Exception($"映射 Hook DLL 以解析 {exportName} 失败");
        }

        try
        {
            var export = NativeMethods.GetProcAddress(module, exportName);
            if (export == 0)
            {
                throw SuspendedGameProcess.NewWin32Exception($"Hook DLL 不包含导出 {exportName}");
            }

            return checked(export - module);
        }
        finally
        {
            _ = NativeMethods.FreeLibrary(module);
        }
    }

    internal static uint RunRemoteThread(
        nint process,
        nint startAddress,
        nint parameter,
        TimeSpan timeout,
        string operation
    )
    {
        var thread = NativeMethods.CreateRemoteThread(process, 0, 0, startAddress, parameter, 0, out _);
        if (thread == 0)
        {
            throw SuspendedGameProcess.NewWin32Exception($"创建远程线程执行 {operation} 失败");
        }

        try
        {
            var milliseconds =
                timeout == Timeout.InfiniteTimeSpan
                    ? NativeMethods.Infinite
                    : checked((uint)Math.Ceiling(timeout.TotalMilliseconds));
            var waitResult = NativeMethods.WaitForSingleObject(thread, milliseconds);
            if (waitResult == NativeMethods.WaitTimeout)
            {
                throw new TimeoutException($"等待远程操作 {operation} 超时。");
            }

            if (waitResult != NativeMethods.WaitObject0)
            {
                if (waitResult == NativeMethods.WaitFailed)
                {
                    throw SuspendedGameProcess.NewWin32Exception($"等待远程操作 {operation} 失败");
                }

                throw new InvalidOperationException($"远程操作 {operation} 返回未知等待状态 0x{waitResult:X8}。");
            }

            if (!NativeMethods.GetExitCodeThread(thread, out var exitCode))
            {
                throw SuspendedGameProcess.NewWin32Exception($"读取远程操作 {operation} 返回值失败");
            }

            if (exitCode == NativeMethods.ThreadStillActive)
            {
                throw new InvalidOperationException($"远程操作 {operation} 在等待结束后仍在运行。");
            }

            return exitCode;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(thread);
        }
    }

    private static void LoadRemoteLibrary(nint process, nint remoteLoadLibrary, string libraryPath)
    {
        var encodedPath = Encoding.Unicode.GetBytes(libraryPath + "\0");
        var remotePath = NativeMethods.VirtualAllocEx(
            process,
            0,
            (nuint)encodedPath.Length,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageReadWrite
        );
        if (remotePath == 0)
        {
            throw SuspendedGameProcess.NewWin32Exception("在游戏进程中分配 Hook 路径失败");
        }

        try
        {
            fixed (byte* source = encodedPath)
            {
                if (
                    !NativeMethods.WriteProcessMemory(
                        process,
                        remotePath,
                        source,
                        (nuint)encodedPath.Length,
                        out var written
                    )
                    || written != (nuint)encodedPath.Length
                )
                {
                    throw SuspendedGameProcess.NewWin32Exception("向游戏进程写入 Hook 路径失败");
                }
            }

            var exitCode = RunRemoteThread(
                process,
                remoteLoadLibrary,
                remotePath,
                TimeSpan.FromSeconds(30),
                LoadLibraryName
            );
            if (exitCode == 0)
            {
                throw new InvalidOperationException("游戏进程中的 LoadLibraryW 返回空模块句柄。");
            }
        }
        finally
        {
            _ = NativeMethods.VirtualFreeEx(process, remotePath, 0, NativeMethods.MemRelease);
        }
    }

    private static nint FindRemoteModuleBase(int processId, string moduleName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        do
        {
            var result = TryFindRemoteModuleBase(processId, moduleName);
            if (result != 0)
            {
                return result;
            }

            Thread.Sleep(25);
        } while (DateTime.UtcNow < deadline);

        throw new InvalidOperationException($"在游戏进程中找不到模块 {moduleName}。");
    }

    private static nint TryFindRemoteModuleBase(int processId, string moduleName)
    {
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(
            NativeMethods.Th32CsSnapModule | NativeMethods.Th32CsSnapModule32,
            checked((uint)processId)
        );
        if (snapshot == NativeMethods.InvalidHandleValue)
        {
            return 0;
        }

        try
        {
            var entry = new NativeMethods.ModuleEntry { Size = checked((uint)sizeof(NativeMethods.ModuleEntry)) };

            if (!NativeMethods.Module32First(snapshot, &entry))
            {
                return 0;
            }

            do
            {
                var currentName = ReadModuleName(&entry);
                if (string.Equals(currentName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.BaseAddress;
                }

                entry.Size = checked((uint)sizeof(NativeMethods.ModuleEntry));
            } while (NativeMethods.Module32Next(snapshot, &entry));

            return 0;
        }
        finally
        {
            _ = NativeMethods.CloseHandle(snapshot);
        }
    }

    private static string ReadModuleName(NativeMethods.ModuleEntry* entry)
    {
        return new string(entry->ModuleName);
    }

    private static string ReadLocalModuleName(nint module)
    {
        const int bufferLength = 32_768;
        var buffer = new char[bufferLength];

        fixed (char* destination = buffer)
        {
            var length = NativeMethods.GetModuleFileName(module, destination, bufferLength);
            if (length == 0 || length >= bufferLength)
            {
                throw SuspendedGameProcess.NewWin32Exception("读取 LoadLibraryW 所属模块名称失败");
            }

            return Path.GetFileName(new string(destination, 0, checked((int)length)));
        }
    }
}
