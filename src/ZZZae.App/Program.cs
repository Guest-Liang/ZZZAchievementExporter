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

ApplicationLog.WriteDiagnostic($"程序退出，代码 {exitCode}。");
return exitCode;
