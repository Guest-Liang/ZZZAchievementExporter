using ZZZae.App.Infrastructure;
using ZZZae.Core.Achievements;
using ZZZae.Core.Profiles;
using ZZZae.Protocol.Achievements;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class AchievementExportSession
{
    private static readonly TimeSpan UidWaitAfterSnapshot = TimeSpan.FromSeconds(30);

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

        ApplicationLog.WriteInfo("游戏已启动且 Hook 已加载。请正常登录，ZZZae 会在取得完整成就快照和当前 UID 后导出");
        ApplicationLog.WriteInfo("按 Ctrl+C 可取消");

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            var captured = await WaitForSnapshotAsync(
                pipe,
                game,
                catalog,
                gameVersion,
                VerifiedAchievementProtocol,
                cancellation.Token
            );
            var snapshot = captured.Snapshot;
            var completedCount = snapshot.Records.Count(static record => record.IsCompleted);

            Console.WriteLine();
            ApplicationLog.WriteInfo(
                $"快照获取完成：UID {captured.Uid}，"
                    + $"识别 {snapshot.Records.Count} 条记录，"
                    + $"其中已完成 {completedCount} 条；"
                    + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                    + $"未知 ID {snapshot.UnknownIdCount} 条"
            );

            ApplicationLog.WriteInfo("正在关闭本次由 ZZZae 启动的游戏...");
            try
            {
                game.Terminate(0);
                ApplicationLog.WriteInfo("游戏已关闭");
            }
            catch (Exception exception)
            {
                ApplicationLog.WriteWarningException("快照已取得，但主动关闭游戏失败", exception);
                ApplicationLog.WriteWarning($"警告：快照已取得，但主动关闭游戏失败：{exception.Message}");
                ApplicationLog.WriteWarning("仍可继续导出；ZZZae 退出时会再次尝试关闭游戏");
            }

            var target = ExportSelectionFlow.Select(cancellation.Token);
            var output = await AchievementExportWriter.WriteAsync(
                snapshot,
                captured.Uid,
                catalog,
                target,
                cancellation.Token
            );

            Console.WriteLine();
            var exportSummary = target switch
            {
                ExportTarget.AchievementBackup =>
                    $"导出完成：完整保留 {snapshot.Records.Count} 条服务端成就记录，其中 {completedCount} 条有完成证据",
                ExportTarget.Liyin =>
                    $"导出完成：写入 {completedCount} 条成就 ID；完整快照共 {snapshot.Records.Count} 条",
                ExportTarget.UiafExperimental => $"导出完成：写入服务端实际返回的 {snapshot.Records.Count} 条成就记录，"
                    + $"其中已完成 {completedCount} 条、未完成 "
                    + $"{snapshot.Records.Count - completedCount} 条",
                _ => throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标"),
            };
            ApplicationLog.WriteInfo(exportSummary);
            ApplicationLog.WriteInfo($"{output.DisplayName}：{output.Path}");

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<CapturedAchievementSnapshot> WaitForSnapshotAsync(
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
            throw new InvalidOperationException("游戏在 Hook 建立连接前退出");
        }

        await connection;
        ApplicationLog.WriteDebug("游戏内 Hook 已连接命名管道", writeToConsole: false);

        var decoder = new AchievementSnapshotDecoder(catalog, gameVersion, verifiedProtocol);
        var ready = false;
        var packetCount = 0;
        AchievementSnapshot? pendingSnapshot = null;
        DateTimeOffset? uidDeadline = null;
        uint currentUid = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CancellationTokenSource? uidWaitCancellation = null;
            var readCancellationToken = cancellationToken;
            if (pendingSnapshot is not null)
            {
                var remaining = uidDeadline!.Value - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new InvalidOperationException("已经取得完整成就快照，但当前游戏 UID 在 30 秒内仍未初始化");
                }

                uidWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                uidWaitCancellation.CancelAfter(remaining);
                readCancellationToken = uidWaitCancellation.Token;
            }

            HookMessage message;
            try
            {
                var read = pipe.ReadMessageAsync(readCancellationToken);
                if (await Task.WhenAny(read, gameExit) == gameExit)
                {
                    var stage = pendingSnapshot is null ? "取得完整成就快照" : "取得当前游戏 UID";
                    throw new InvalidOperationException($"游戏在{stage}前退出");
                }

                message = await read;
            }
            catch (OperationCanceledException)
                when (pendingSnapshot is not null && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("已经取得完整成就快照，但当前游戏 UID 在 30 秒内仍未初始化");
            }
            catch (EndOfStreamException exception)
            {
                var reason = gameExit.IsCompleted ? "游戏已经退出" : "游戏内 Hook 提前关闭了通信通道";
                var missing = pendingSnapshot is null
                    ? "尚未确认完整成就快照"
                    : "完整成就快照已经确认，但尚未取得当前游戏 UID";
                throw new InvalidOperationException(
                    $"{reason}；此前共收到并检查 {packetCount} 个完整明文包，{missing}",
                    exception
                );
            }
            finally
            {
                uidWaitCancellation?.Dispose();
            }

            switch (message)
            {
                case HookReadyMessage hookReady:
                    ready = true;
                    ApplicationLog.WriteInfo("Hook 已就绪");
                    ApplicationLog.WriteDebug(
                        "Hook 定位详情：明文解析器 Offsets "
                            + $"RVA 0x{hookReady.ParserRva:X}，"
                            + $"定位版本 {hookReady.ParserLocatorVersion}；"
                            + "UID Offsets "
                            + $"RVA 0x{hookReady.UidRootSlotRva:X}，"
                            + $"定位版本 {hookReady.UidLocatorVersion}，"
                            + $"{hookReady.EquivalentUidPathCount} 条等价路径",
                        writeToConsole: true
                    );
                    break;

                case HookUidMessage uid:
                    if (!ready)
                    {
                        throw new InvalidDataException("Hook 在就绪确认前发送了 UID 状态");
                    }

                    if (uid.Uid != 0 && uid.Uid != currentUid)
                    {
                        ApplicationLog.WriteInfo($"已读取当前游戏 UID：{uid.Uid}");
                    }

                    currentUid = uid.Uid;
                    if (pendingSnapshot is not null && currentUid != 0)
                    {
                        return new CapturedAchievementSnapshot(pendingSnapshot, currentUid);
                    }

                    break;

                case HookPacketMessage packet:
                    if (!ready)
                    {
                        throw new InvalidDataException("Hook 在就绪确认前发送了数据包");
                    }

                    packetCount++;
                    if (packetCount == 1)
                    {
                        ApplicationLog.WriteInfo("已收到第一个包");
                        ApplicationLog.WriteDebug(
                            $"第一个包详情：命令 {packet.Packet.CommandId}，包体 {packet.Packet.Body.Length} bytes",
                            writeToConsole: true
                        );
                    }

                    if (
                        pendingSnapshot is null
                        && decoder.TryDecode(packet.Packet, out var snapshot)
                        && snapshot is not null
                    )
                    {
                        ApplicationLog.WriteInfo("已确认成就记录结构");
                        ApplicationLog.WriteDebug(
                            "成就记录结构详情："
                                + $"（命令 {snapshot.SourceCommandId}，"
                                + $"路径 {snapshot.RecordFieldPath}，"
                                + $"记录 {snapshot.Records.Count} 条，"
                                + $"元数据命中 {snapshot.CatalogMatchCount} 条，"
                                + $"未知 ID {snapshot.UnknownIdCount} 条）",
                            writeToConsole: true
                        );

                        if (packet.Uid != 0)
                        {
                            return new CapturedAchievementSnapshot(snapshot, packet.Uid);
                        }

                        pendingSnapshot = snapshot;
                        uidDeadline = DateTimeOffset.UtcNow + UidWaitAfterSnapshot;
                        currentUid = 0;
                        ApplicationLog.WriteInfo("成就快照已取得；当前 UID 尚未初始化，继续等待最多 30 秒...");
                    }

                    if (pendingSnapshot is null && packetCount % 100 == 0)
                    {
                        ApplicationLog.WriteDebug(
                            $"已检查 {packetCount} 个包，继续等待成就快照...",
                            writeToConsole: true
                        );
                    }

                    break;

                case HookErrorMessage error:
                    throw new InvalidOperationException($"游戏内 Hook 报错：{error.Error}");

                default:
                    throw new InvalidDataException("收到无法识别的 Hook 消息");
            }
        }
    }
}

internal sealed record CapturedAchievementSnapshot(AchievementSnapshot Snapshot, uint Uid);
