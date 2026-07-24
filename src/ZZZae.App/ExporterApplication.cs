using System.Diagnostics;
using ZZZae.App.Infrastructure;
using ZZZae.Core.Achievements;
using ZZZae.Core.Profiles;
using ZZZae.Formats.Backup;
using ZZZae.Formats.Liyin;
using ZZZae.Protocol.Achievements;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class ExporterApplication
{
    internal const int UserRequestedExitCode = 4;
    internal const int RelaunchedAsAdministratorExitCode = 5;

    private const string GameProcessName = "ZenlessZoneZero";
    private const string ChinaProductionMarker = "CNPRODWin";

    private static readonly TimeSpan ChinaStandardOffset = TimeSpan.FromHours(8);

    private static readonly AchievementProtocolProfile VerifiedAchievementProtocol = new()
    {
        FullSnapshotCommandId = 3692,
        RecordFieldPath = "$.11.778.9[]",
        IdFieldNumber = 1,
        FinishTimestampFieldNumber = 3,
        CompletedFlagFieldNumber = 4,
    };

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

        var gamePath = LocateGame(configuredGamePath);
        if (gamePath is null)
        {
            ApplicationLog.WriteDiagnostic("用户在游戏路径选择阶段退出。");
            return UserRequestedExitCode;
        }

        var gameVersion = ValidateChinaProductionBuild(gamePath);
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

        using var game = SuspendedGameProcess.Start(gamePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        game.Resume();
        using var hook = RemoteHookInjector.Inject(game, hookPath);

        Console.WriteLine("游戏已启动且 Hook 已加载。请正常登录，ZZZae 会在识别到完整成就快照后立即导出。");
        Console.WriteLine("按 Ctrl+C 可取消。");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var snapshot = await WaitForSnapshotAsync(
                pipe,
                game,
                catalog,
                gameVersion,
                VerifiedAchievementProtocol,
                cancellation.Token
            );
            var outputs = await ExportSnapshotAsync(snapshot, catalog, cancellation.Token);

            var completedCount = snapshot.Records.Count(static record => record.IsCompleted);

            Console.WriteLine();
            Console.WriteLine(
                $"导出完成：识别 {snapshot.Records.Count} 条记录，"
                    + $"其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                    + $"未知 ID {snapshot.UnknownIdCount} 条。"
            );
            Console.WriteLine($"完整备份：{outputs.FullBackup}");
            Console.WriteLine($"Liyin 文件：{outputs.Liyin}");

            Console.WriteLine("成就文件已写入，正在关闭本次由 ZZZae 启动的游戏……");
            try
            {
                game.Terminate(0);
                Console.WriteLine("游戏已关闭。");
            }
            catch (Exception exception)
            {
                ApplicationLog.WriteException("成就已成功导出，但主动关闭游戏失败。", exception);
                Console.Error.WriteLine($"警告：成就已成功导出，但主动关闭游戏失败：{exception.Message}");
                Console.Error.WriteLine("ZZZae 退出时会再次尝试关闭游戏；如果游戏仍在运行，请手动退出。");
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static string? LocateGame(string? configuredGamePath)
    {
        if (configuredGamePath is not null)
        {
            Console.WriteLine("游戏路径来源：命令行 --game");
            return GameLocator.ResolveChinaGameExecutable(configuredGamePath);
        }

        var registryGamePath = GameLocator.TryFindChinaGameExecutable();
        if (Console.IsInputRedirected)
        {
            if (registryGamePath is not null)
            {
                Console.WriteLine("游戏路径来源：注册表（非交互启动）");
                return registryGamePath;
            }

            throw MissingRegistryGamePath();
        }

        while (true)
        {
            Console.WriteLine("请选择游戏路径获取方式（↑/↓ 选择，Enter 确认）：");
            var isAdministrator = ElevationManager.IsAdministrator();
            var options = new[]
            {
                registryGamePath is null ? "从注册表读取游戏路径（未检测到有效路径）" : "从注册表读取游戏路径",
                isAdministrator
                    ? "手动粘贴游戏目录 / ZenlessZoneZero.exe"
                    : "手动粘贴或拖入游戏目录 / ZenlessZoneZero.exe",
                "退出 ZZZae",
            };
            var selected = ReadSelectionMenu(options, registryGamePath is null ? 1 : 0);
            Console.WriteLine($"已选择：{options[selected]}");

            if (selected == 0)
            {
                registryGamePath = GameLocator.TryFindChinaGameExecutable();
                if (registryGamePath is not null)
                {
                    Console.WriteLine("游戏路径来源：注册表");
                    return registryGamePath;
                }

                Console.Error.WriteLine("未检测到有效的游戏注册表路径，请重新选择。");
                Console.WriteLine();
                continue;
            }

            if (selected == 1)
            {
                if (isAdministrator)
                {
                    Console.WriteLine(
                        "当前窗口具有管理员权限，Windows 会阻止从普通权限资源管理器拖入；请复制并粘贴完整路径。"
                    );
                }
                else
                {
                    Console.WriteLine("可以把游戏目录或 ZenlessZoneZero.exe 拖入当前窗口，也可以粘贴完整路径。");
                }

                Console.Write("游戏路径（直接按 Enter 取消）：");
                var enteredPath = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(enteredPath))
                {
                    return null;
                }

                Console.WriteLine("游戏路径来源：交互输入");
                return GameLocator.ResolveChinaGameExecutable(enteredPath);
            }

            return null;
        }
    }

    private static int ReadSelectionMenu(IReadOnlyList<string> options, int selected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(selected);
        if (options.Count == 0 || selected >= options.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selected));
        }

        var menuTop = Console.CursorTop;
        while (true)
        {
            RenderSelectionMenu(options, selected, menuTop);

            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.UpArrow:
                    selected = selected == 0 ? options.Count - 1 : selected - 1;
                    break;

                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % options.Count;
                    break;

                case ConsoleKey.Enter:
                    Console.SetCursorPosition(0, menuTop + options.Count);
                    return selected;

                case ConsoleKey.Escape:
                    Console.SetCursorPosition(0, menuTop + options.Count);
                    return options.Count - 1;
            }
        }
    }

    private static void RenderSelectionMenu(IReadOnlyList<string> options, int selected, int menuTop)
    {
        var clearWidth = Math.Max(1, Console.BufferWidth - 1);
        using var output = new StreamWriter(
            Console.OpenStandardOutput(),
            Console.OutputEncoding,
            bufferSize: 256,
            leaveOpen: true
        )
        {
            AutoFlush = true,
        };

        for (var index = 0; index < options.Count; index++)
        {
            Console.SetCursorPosition(0, menuTop + index);
            output.Write(new string(' ', clearWidth));
            Console.SetCursorPosition(0, menuTop + index);
            output.Write(index == selected ? $"> {options[index]}" : $"  {options[index]}");
        }

        Console.SetCursorPosition(0, menuTop + options.Count);
    }

    private static FileNotFoundException MissingRegistryGamePath()
    {
        return new FileNotFoundException(
            """
            没有在注册表 HKCU\Software\miHoYo\HYP\1_1\nap_cn 的 GameInstallPath 找到国服游戏。
            非交互启动时请使用 --game 指定游戏目录或 ZenlessZoneZero.exe 完整路径。
            """
        );
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

    private static async Task<AchievementSnapshot> WaitForSnapshotAsync(
        HookPipeServer pipe,
        SuspendedGameProcess game,
        AchievementCatalog catalog,
        string gameVersion,
        AchievementProtocolProfile verifiedProtocol,
        CancellationToken cancellationToken
    )
    {
        var gameExit = game.WaitForExitAsync(CancellationToken.None);
        var connection = pipe.WaitForConnectionAsync(cancellationToken);

        if (await Task.WhenAny(connection, gameExit) == gameExit)
        {
            throw new InvalidOperationException("游戏在 Hook 建立连接前退出。");
        }

        await connection;

        var decoder = new AchievementSnapshotDecoder(catalog, gameVersion, verifiedProtocol);
        var ready = false;
        var packetCount = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = pipe.ReadMessageAsync(cancellationToken);
            if (await Task.WhenAny(read, gameExit) == gameExit)
            {
                throw new InvalidOperationException("游戏在取得完整成就快照前退出。");
            }

            HookMessage message;
            try
            {
                message = await read;
            }
            catch (EndOfStreamException exception)
            {
                var reason = gameExit.IsCompleted ? "游戏已经退出" : "游戏内 Hook 提前关闭了通信通道";
                throw new InvalidOperationException(
                    $"{reason}；此前共收到并检查 {packetCount} 个完整明文包，但尚未确认完整成就快照。",
                    exception
                );
            }

            switch (message)
            {
                case HookReadyMessage hookReady:
                    ready = true;
                    Console.WriteLine(
                        "Hook 已就绪：明文解析器 "
                            + $"RVA 0x{hookReady.ParserRva:X}，"
                            + $"特征版本 {hookReady.PatternVersion}。"
                    );
                    break;

                case HookPacketMessage packet:
                    if (!ready)
                    {
                        throw new InvalidDataException("Hook 在就绪确认前发送了数据包。");
                    }

                    packetCount++;
                    if (packetCount == 1)
                    {
                        Console.WriteLine(
                            "已收到第一个完整明文包："
                                + $"命令 {packet.Packet.CommandId}，"
                                + $"包体 {packet.Packet.Body.Length} bytes。"
                        );
                    }

                    if (decoder.TryDecode(packet.Packet, out var snapshot) && snapshot is not null)
                    {
                        Console.WriteLine(
                            "已从完整解密包中确认成就记录结构"
                                + $"（命令 {snapshot.SourceCommandId}，"
                                + $"路径 {snapshot.RecordFieldPath}）。"
                        );
                        return snapshot;
                    }

                    if (packetCount % 100 == 0)
                    {
                        Console.WriteLine($"已检查 {packetCount} 个完整明文包，继续等待成就快照……");
                    }

                    break;

                case HookErrorMessage error:
                    throw new InvalidOperationException($"游戏内 Hook 报错：{error.Error}");

                default:
                    throw new InvalidDataException("收到无法识别的 Hook 消息。");
            }
        }
    }

    private static async Task<ExportPaths> ExportSnapshotAsync(
        AchievementSnapshot snapshot,
        AchievementCatalog catalog,
        CancellationToken cancellationToken
    )
    {
        var stamp = snapshot.CapturedAt.ToOffset(ChinaStandardOffset).ToString("yyyyMMdd-HHmmss");
        var directory = Environment.CurrentDirectory;
        var fullBackupPath = UniquePath(directory, $"ZZZae-full-{stamp}.json");
        var liyinPath = UniquePath(directory, $"ZZZae-liyin-{stamp}.json");

        var fullBackup = FullBackupExporter.Serialize(snapshot, catalog.LatestVersion, catalog.Count);
        var liyin = LiyinExporter.Serialize(snapshot);

        await AtomicFile.WriteAllTextAsync(fullBackupPath, fullBackup, cancellationToken);

        try
        {
            await AtomicFile.WriteAllTextAsync(liyinPath, liyin, cancellationToken);
        }
        catch
        {
            try
            {
                File.Delete(fullBackupPath);
            }
            catch (IOException)
            {
                // Preserve the valid full backup if rollback fails.
            }
            catch (UnauthorizedAccessException)
            {
                // Same as above.
            }

            throw;
        }

        return new ExportPaths(fullBackupPath, liyinPath);
    }

    private static string UniquePath(string directory, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var suffix = 2; suffix < 10_000; suffix++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{suffix}{extension}");
            if (!File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new IOException("无法为导出结果选择未占用的文件名。");
    }

    private static string ValidateChinaProductionBuild(string gameExecutablePath)
    {
        var gameDirectory =
            Path.GetDirectoryName(gameExecutablePath) ?? throw new InvalidDataException("无法确定游戏安装目录。");
        var versionPath = Path.Combine(gameDirectory, "version_info");
        if (!File.Exists(versionPath))
        {
            throw new FileNotFoundException("游戏目录缺少 version_info，无法确认国服正式渠道。", versionPath);
        }

        var buildMarker = File.ReadAllText(versionPath).Trim();
        if (!buildMarker.StartsWith(ChinaProductionMarker, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"当前构建标记为 {buildMarker}，不是 ZZZae 支持的国服 Windows 正式渠道。");
        }

        var gameAssemblyPath = Path.Combine(gameDirectory, "GameAssembly.dll");
        if (!File.Exists(gameAssemblyPath))
        {
            throw new FileNotFoundException("游戏目录缺少 GameAssembly.dll。", gameAssemblyPath);
        }

        // Do not reject a build by whole-file hash or fixed RVA.
        // Harmless hot updates may change either. The injected hook
        // instead requires a unique executable-section signature plus
        // the packet framing magic, and the host requires the verified
        // full-snapshot command and protobuf record structure.
        return buildMarker;
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

    private sealed record ExportPaths(string FullBackup, string Liyin);
}
