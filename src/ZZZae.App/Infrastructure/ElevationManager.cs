using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ZZZae.App.Infrastructure;

internal static class ElevationManager
{
    private const int OperationCanceledError = 1223;

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RelaunchAsAdministrator(string gameExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameExecutablePath);

        var executablePath =
            Environment.ProcessPath ?? throw new InvalidOperationException("无法确定 ZZZae 可执行文件路径。");
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory,
        };
        startInfo.ArgumentList.Add("--game");
        startInfo.ArgumentList.Add(Path.GetFullPath(gameExecutablePath));

        try
        {
            using var process =
                Process.Start(startInfo) ?? throw new InvalidOperationException("Windows 没有启动管理员权限实例。");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == OperationCanceledError)
        {
            throw new OperationCanceledException("用户取消了管理员权限请求。", exception);
        }
    }
}
