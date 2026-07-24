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

        if (args.Length != 0)
        {
            Console.Error.WriteLine("ZZZae 不需要命令行参数；请直接运行 ZZZae.exe。");
            return 2;
        }

        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
        {
            Console.Error.WriteLine("ZZZae 只支持 Windows x64。");
            return 2;
        }

        try
        {
            return await ExportAsync();
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

    private static async Task<int> ExportAsync()
    {
        EnsureGameIsNotRunning();

        var gamePath =
            GameLocator.TryFindChinaGameExecutable()
            ?? throw new FileNotFoundException(
                @"没有在注册表 HKCU\Software\miHoYo\HYP\1_1\nap_cn 的 GameInstallPath 找到国服游戏。"
            );
        var hookPath =
            EmbeddedHook.TryExtract()
            ?? throw new InvalidOperationException(
                @"当前构建没有内嵌 Hook DLL。请运行 publish.ps1 后使用 artifacts\publish\ZZZae.exe。"
            );
        var catalog = AchievementCatalog.LoadBundled();
        var gameVersion = ValidateChinaProductionBuild(gamePath);

        Console.WriteLine($"游戏：{gamePath}");
        Console.WriteLine($"游戏构建：{gameVersion}（国服）");
        Console.WriteLine($"成就元数据：{catalog.LatestVersion}" + $"（{catalog.Count} 项）");
        Console.WriteLine("正在创建游戏进程并安装轻量 Hook……");

        using var game = SuspendedGameProcess.Start(gamePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        game.Resume();
        using var hook = RemoteHookInjector.Inject(game, hookPath);
        game.KeepRunningOnDispose();

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
            var hookShutdownConfirmed = true;
            try
            {
                hook.Shutdown();
            }
            catch (Exception exception)
            {
                hookShutdownConfirmed = false;
                Console.Error.WriteLine(
                    "警告：主动停用 Hook 失败，将通过关闭命名管道触发游戏侧清理：" + exception.Message
                );
            }

            Console.WriteLine();
            Console.WriteLine(
                $"导出完成：识别 {snapshot.Records.Count} 条记录，"
                    + $"其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                    + $"未知 ID {snapshot.UnknownIdCount} 条。"
            );
            Console.WriteLine($"完整备份：{outputs.FullBackup}");
            Console.WriteLine($"Liyin 文件：{outputs.Liyin}");
            Console.WriteLine(
                hookShutdownConfirmed
                    ? "Hook 已停用，游戏会继续运行。"
                    : "命名管道将在程序退出时关闭，游戏侧会停用 Hook 并继续运行。"
            );
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
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
