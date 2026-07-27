using ZZZae.App.Infrastructure;
using ZZZae.Core.Achievements;
using ZZZae.Formats.Backup;
using ZZZae.Formats.Liyin;
using ZZZae.Formats.Uiaf;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class AchievementExportWriter
{
    private static readonly TimeSpan ChinaStandardOffset = TimeSpan.FromHours(8);

    public static async Task<ExportResult> WriteAsync(
        AchievementSnapshot snapshot,
        AchievementCatalog catalog,
        ExportTarget target,
        CancellationToken cancellationToken
    )
    {
        var stamp = snapshot.CapturedAt.ToOffset(ChinaStandardOffset).ToString("yyyyMMdd-HHmmss");
        var directory = Environment.CurrentDirectory;
        string fileName;
        string displayName;
        string content;
        switch (target)
        {
            case ExportTarget.AchievementBackup:
                fileName = $"ZZZae-achievements-{stamp}.json";
                displayName = "成就数据备份";
                content = AchievementBackupExporter.Serialize(
                    snapshot,
                    catalog.LatestVersion,
                    catalog.Count
                );
                break;

            case ExportTarget.Liyin:
                fileName = $"ZZZae-liyin-{stamp}.json";
                displayName = "Liyin 格式";
                content = LiyinExporter.Serialize(snapshot);
                break;

            case ExportTarget.UiafExperimental:
                fileName = $"ZZZae-uiaf-{stamp}.json";
                displayName = "实验性 UIAF（非官方）";
                content = UiafExporter.Serialize(snapshot);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "未知导出目标。");
        }

        var outputPath = UniquePath(directory, fileName);
        await AtomicFile.WriteAllTextAsync(outputPath, content, cancellationToken);
        return new ExportResult(displayName, outputPath);
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
}

internal sealed record ExportResult(string DisplayName, string Path);
