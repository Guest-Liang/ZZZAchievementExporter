using ZZZae.App.Infrastructure;
using ZZZae.Core.Achievements;
using ZZZae.Core.Profiles;
using ZZZae.Protocol.Achievements;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class AchievementExportSession
{
    private static readonly AchievementProtocolProfile VerifiedAchievementProtocol = new()
    {
        FullSnapshotCommandId = 3692,
        RecordFieldPath = "$.11.778.9[]",
        IdFieldNumber = 1,
        FinishTimestampFieldNumber = 3,
        CompletedFlagFieldNumber = 4,
    };

    public static async Task<int> RunAsync(
        string gamePath,
        string gameVersion,
        string hookPath,
        AchievementCatalog catalog
    )
    {
        using var game = SuspendedGameProcess.Start(gamePath);
        await using var pipe = new HookPipeServer(game.ProcessId);

        game.Resume();
        RemoteHookInjector.Inject(game, hookPath);

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
            var outputs = await AchievementExportWriter.WriteAsync(snapshot, catalog, cancellation.Token);

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
}
