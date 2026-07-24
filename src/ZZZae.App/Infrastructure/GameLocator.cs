using Microsoft.Win32;

namespace ZZZae.App.Infrastructure;

internal static class GameLocator
{
    private const string RegistryPath =
        @"Software\miHoYo\HYP\1_1\nap_cn";

    private const string InstallPathValue = "GameInstallPath";
    private const string GameExecutableName = "ZenlessZoneZero.exe";

    public static string? TryFindChinaGameExecutable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        if (key?.GetValue(InstallPathValue) is not string configuredPath
            || string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        var normalized = Environment.ExpandEnvironmentVariables(
            configuredPath.Trim().Trim('"'));

        var executablePath = normalized.EndsWith(
            ".exe",
            StringComparison.OrdinalIgnoreCase)
            ? normalized
            : Path.Combine(normalized, GameExecutableName);

        return File.Exists(executablePath)
            ? Path.GetFullPath(executablePath)
            : null;
    }
}
