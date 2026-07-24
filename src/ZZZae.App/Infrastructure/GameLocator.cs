using Microsoft.Win32;

namespace ZZZae.App.Infrastructure;

internal static class GameLocator
{
    private const string RegistryPath = @"Software\miHoYo\HYP\1_1\nap_cn";

    private const string InstallPathValue = "GameInstallPath";
    private const string GameExecutableName = "ZenlessZoneZero.exe";

    public static string? TryFindChinaGameExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key?.GetValue(InstallPathValue) is not string configuredPath || string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            return ResolveChinaGameExecutable(configuredPath);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static string ResolveChinaGameExecutable(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException(
                "--game 后必须提供游戏目录或 ZenlessZoneZero.exe 路径。",
                nameof(configuredPath)
            );
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
        var fullPath = Path.GetFullPath(expandedPath);
        string executablePath;

        if (Directory.Exists(fullPath))
        {
            executablePath = Path.Combine(fullPath, GameExecutableName);
        }
        else if (File.Exists(fullPath))
        {
            if (!Path.GetFileName(fullPath).Equals(GameExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"--game 指向的文件必须名为 {GameExecutableName}。");
            }

            executablePath = fullPath;
        }
        else
        {
            throw new FileNotFoundException($"--game 指定的路径不存在，或目录中没有 {GameExecutableName}。", fullPath);
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"指定的游戏目录中没有 {GameExecutableName}。", executablePath);
        }

        return Path.GetFullPath(executablePath);
    }
}
