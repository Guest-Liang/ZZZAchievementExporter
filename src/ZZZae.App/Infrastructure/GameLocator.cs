using System.Security;
using Microsoft.Win32;

namespace ZZZae.App.Infrastructure;

internal static class GameLocator
{
    private const string RegistryPath = @"Software\miHoYo\HYP\1_1\nap_cn";

    private const string InstallPathValue = "GameInstallPath";
    private const string GameExecutableName = "ZenlessZoneZero.exe";

    public static string? TryFindChinaGameExecutable()
    {
        var valuePath = $@"HKCU\{RegistryPath}\{InstallPathValue}";
        ApplicationLog.WriteInfo($"正在读取游戏注册表项：{valuePath}", writeToConsole: false);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            if (key is null)
            {
                ApplicationLog.WriteWarning($@"游戏注册表键不存在：HKCU\{RegistryPath}", writeToConsole: false);
                return null;
            }

            var value = key.GetValue(
                InstallPathValue,
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames
            );
            if (value is not string configuredPath || string.IsNullOrWhiteSpace(configuredPath))
            {
                var valueState = value is null ? "不存在" : $"类型为 {value.GetType().FullName}，不是字符串";
                ApplicationLog.WriteWarning($"游戏注册表值不可用：{valuePath}（{valueState}）", writeToConsole: false);
                return null;
            }

            ApplicationLog.WriteInfo($"游戏注册表值：{valuePath} = {configuredPath}", writeToConsole: false);

            try
            {
                var executablePath = ResolveChinaGameExecutable(configuredPath);
                ApplicationLog.WriteInfo($"注册表游戏路径解析结果：{executablePath}", writeToConsole: false);
                return executablePath;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
            {
                ApplicationLog.WriteWarning($"注册表游戏路径无法使用：{exception.Message}", writeToConsole: false);
                ApplicationLog.WriteDebug($"注册表游戏路径解析异常：{exception}", writeToConsole: false);
                return null;
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            ApplicationLog.WriteWarning($"读取游戏注册表项失败：{exception.Message}", writeToConsole: false);
            ApplicationLog.WriteDebug($"注册表读取异常：{exception}", writeToConsole: false);
            return null;
        }
    }

    public static string ResolveChinaGameExecutable(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new ArgumentException("--game 后必须提供游戏目录或 ZenlessZoneZero.exe 路径", nameof(configuredPath));
        }

        var normalizedPath = configuredPath.Trim();
        if (
            normalizedPath.Length >= 2
            && (
                (normalizedPath[0] == '"' && normalizedPath[^1] == '"')
                || (normalizedPath[0] == '\'' && normalizedPath[^1] == '\'')
            )
        )
        {
            normalizedPath = normalizedPath[1..^1];
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(normalizedPath);
        var fullPath = Path.GetFullPath(expandedPath);
        ApplicationLog.WriteDebug(
            $"游戏路径规范化：输入 {configuredPath}；展开后 {expandedPath}；完整路径 {fullPath}",
            writeToConsole: false
        );
        string executablePath;

        if (Directory.Exists(fullPath))
        {
            executablePath = Path.Combine(fullPath, GameExecutableName);
        }
        else if (File.Exists(fullPath))
        {
            if (!Path.GetFileName(fullPath).Equals(GameExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"--game 指向的文件必须名为 {GameExecutableName}");
            }

            executablePath = fullPath;
        }
        else
        {
            throw new FileNotFoundException($"--game 指定的路径不存在，或目录中没有 {GameExecutableName}", fullPath);
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException($"指定的游戏目录中没有 {GameExecutableName}", executablePath);
        }

        var resolvedPath = Path.GetFullPath(executablePath);
        ApplicationLog.WriteDebug($"游戏可执行文件解析结果：{resolvedPath}", writeToConsole: false);
        return resolvedPath;
    }
}
