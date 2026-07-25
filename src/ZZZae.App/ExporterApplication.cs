using System.Diagnostics;
using ZZZae.App.Infrastructure;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class ExporterApplication
{
    internal const int UserRequestedExitCode = 4;
    internal const int RelaunchedAsAdministratorExitCode = 5;

    private const string GameProcessName = "ZenlessZoneZero";

    public static async Task<int> RunAsync(string[] args)
    {
        Console.WriteLine("ZZZae — 绝区零成就导出");
        Console.WriteLine("https://github.com/Guest-Liang/ZZZAchievementExporter");
        Console.WriteLine();
        Console.WriteLine(
            """
            免责声明：本工具是非官方第三方工具，运行时会向游戏进程加载临时 Hook，
            可能违反游戏规则或被反作弊系统识别，并可能导致账号限制或封禁。
            使用者自行判断并承担全部风险，作者及贡献者不对账号处罚或其他损失负责。
            若无法接受上述风险，请立即关闭本程序。
            """
        );
        if (ApplicationLog.CurrentFilePath is { } logPath)
        {
            Console.WriteLine($"运行日志：{logPath}");
        }
        Console.WriteLine();

        if (!TryParseArguments(args, out var configuredGamePath, out var argumentError))
        {
            Console.Error.WriteLine(argumentError);
            WriteUsage();
            return 2;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("ZZZae 只支持 Windows x64。");
            return 2;
        }

        try
        {
            return await ExportAsync(configuredGamePath);
        }
        catch (OperationCanceledException)
        {
            ApplicationLog.WriteDiagnostic("用户取消导出；没有写出不完整的成就文件。");
            Console.Error.WriteLine("已取消；没有导出不完整的成就文件。");
            return 3;
        }
        catch (Exception exception)
        {
            ApplicationLog.WriteException("导出失败。", exception);
            Console.Error.WriteLine();
            Console.Error.WriteLine($"导出失败：{exception.Message}");
            Console.Error.WriteLine("没有导出不完整的成就文件。");
            if (ApplicationLog.CurrentFilePath is { } failureLogPath)
            {
                Console.Error.WriteLine($"详细信息已写入：{failureLogPath}");
            }
            return 1;
        }
    }

    private static async Task<int> ExportAsync(string? configuredGamePath)
    {
        EnsureGameIsNotRunning();

        var gameSelection = GameSelectionFlow.Select(configuredGamePath);
        if (gameSelection is null)
        {
            ApplicationLog.WriteDiagnostic("用户在游戏路径选择阶段退出。");
            return UserRequestedExitCode;
        }

        var gamePath = gameSelection.ExecutablePath;
        var gameVersion = gameSelection.Version;
        if (!ElevationManager.IsAdministrator())
        {
            Console.WriteLine($"游戏：{gamePath}");
            Console.WriteLine($"游戏构建：{gameVersion}（国服）");
            Console.WriteLine("游戏路径已确认，正在申请管理员权限……");
            ElevationManager.RelaunchAsAdministrator(gamePath);
            Console.WriteLine("管理员权限实例已启动，当前窗口即将关闭。");
            return RelaunchedAsAdministratorExitCode;
        }

        var hookPath =
            EmbeddedHook.TryExtract()
            ?? throw new InvalidOperationException(
                @"当前构建没有内嵌 Hook DLL。请运行 publish.ps1 后使用 artifacts\publish\ZZZae.exe。"
            );
        var catalog = AchievementCatalog.LoadBundled();

        Console.WriteLine($"游戏：{gamePath}");
        Console.WriteLine($"游戏构建：{gameVersion}（国服）");
        Console.WriteLine($"成就元数据：{catalog.LatestVersion}" + $"（{catalog.Count} 项）");
        Console.WriteLine("正在创建游戏进程并安装轻量 Hook……");

        return await AchievementExportSession.RunAsync(gamePath, gameVersion, hookPath, catalog);
    }

    private static bool TryParseArguments(string[] args, out string? configuredGamePath, out string? error)
    {
        configuredGamePath = null;
        error = null;

        if (args.Length == 0)
        {
            return true;
        }

        if (!args[0].Equals("--game", StringComparison.Ordinal))
        {
            error = "无法识别命令行参数。ZZZae 只支持可选参数 --game。";
            return false;
        }

        if (args.Length == 1 || string.IsNullOrWhiteSpace(args[1]))
        {
            error = "--game 后必须提供游戏目录或 ZenlessZoneZero.exe 路径。";
            return false;
        }

        if (args.Length != 2)
        {
            error = "--game 只能指定一个游戏目录或 ZenlessZoneZero.exe 路径。";
            return false;
        }

        configuredGamePath = args[1];
        return true;
    }

    private static void WriteUsage()
    {
        Console.Error.WriteLine(@"用法：ZZZae.exe [--game ""游戏目录或 ZenlessZoneZero.exe 路径""]");
    }

    private static void EnsureGameIsNotRunning()
    {
        var processes = Process.GetProcessesByName(GameProcessName);
        try
        {
            if (processes.Length != 0)
            {
                throw new InvalidOperationException("检测到绝区零已经在运行。请先完全退出游戏，再运行 ZZZae。");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
