using System.Text;
using ZZZae.App;
using ZZZae.App.Infrastructure;

Console.OutputEncoding = Encoding.UTF8;
using var applicationLog = ApplicationLog.TryStart();

int exitCode;
try
{
    exitCode = await ExporterApplication.RunAsync(args);
}
catch (Exception exception)
{
    ApplicationLog.WriteException("程序入口发生未处理异常。", exception);
    Console.Error.WriteLine();
    Console.Error.WriteLine($"程序发生未处理异常：{exception.Message}");
    Console.Error.WriteLine("请将 EXE 同目录的 ZZZae.log 提供给开发者排查。");
    exitCode = 1;
}

if (exitCode == ExporterApplication.UserRequestedExitCode)
{
    ApplicationLog.WriteDiagnostic("用户选择退出，程序正常结束。");
    return 0;
}

if (exitCode == ExporterApplication.RelaunchedAsAdministratorExitCode)
{
    ApplicationLog.WriteDiagnostic("已把导出流程交给管理员权限实例，当前实例正常结束。");
    return 0;
}

ApplicationLog.WriteDiagnostic($"程序退出，代码 {exitCode}。");
WaitForExitAcknowledgement(exitCode);
return exitCode;

static void WaitForExitAcknowledgement(int exitCode)
{
    if (Console.IsInputRedirected)
    {
        return;
    }

    Console.WriteLine();
    Console.Write(
        exitCode == 0
            ? "当前成就导出成功，按 Enter 退出 ZZZae……"
            : "当前成就未成功导出，请检查上方提示和 ZZZae.log。按 Enter 退出 ZZZae……"
    );

    try
    {
        _ = Console.ReadLine();
    }
    catch (IOException exception)
    {
        ApplicationLog.WriteException("等待用户确认退出时读取控制台失败。", exception);
    }
    catch (InvalidOperationException exception)
    {
        ApplicationLog.WriteException("等待用户确认退出时控制台不可用。", exception);
    }
}
