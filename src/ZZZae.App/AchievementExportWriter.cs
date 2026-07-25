using ZZZae.App.Infrastructure;
using ZZZae.Core.Achievements;
using ZZZae.Formats.Backup;
using ZZZae.Formats.Liyin;
using ZZZae.Protocol.Metadata;

namespace ZZZae.App;

internal static class AchievementExportWriter
{
    private static readonly TimeSpan ChinaStandardOffset = TimeSpan.FromHours(8);

    public static async Task<ExportPaths> WriteAsync(
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
}

internal sealed record ExportPaths(string FullBackup, string Liyin);
