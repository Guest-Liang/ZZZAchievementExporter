using System.Runtime.InteropServices;

namespace ZZZae.Hook;

public static class HookExports
{
    private static int _started;
    private static Thread? _worker;

    [UnmanagedCallersOnly(EntryPoint = "ZZZaeHookMain")]
    public static int Start(nint bootstrapContext)
    {
        _ = bootstrapContext;

        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return 1;
        }

        _worker = new Thread(Run) { IsBackground = true, Name = "ZZZae Hook Worker" };
        _worker.Start();
        return 0;
    }

    private static void Run()
    {
        try
        {
            FrameTransport.Connect();
            var hookRva = PacketHook.WaitForModuleAndInstall(TimeSpan.FromMinutes(2));
            FrameTransport.SendReady(hookRva);
            FrameTransport.Pump();
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown before GameAssembly.dll was loaded.
        }
        catch (Exception exception)
        {
            FrameTransport.TrySendError(exception.ToString());
        }
        finally
        {
            FrameTransport.RequestShutdown();
            PacketHook.Uninstall();
            FrameTransport.Disconnect();
        }
    }
}
