namespace ZZZae.App;

internal static class ExportSelectionFlow
{
    public static ExportTarget Select(CancellationToken cancellationToken)
    {
        if (Console.IsInputRedirected)
        {
            Console.WriteLine("标准输入不可交互，默认导出成就数据备份。");
            return ExportTarget.AchievementBackup;
        }

        Console.WriteLine();
        Console.WriteLine("请选择导出格式（↑/↓ 选择，Enter 确认）：");
        var options = new[]
        {
            "成就数据备份（保留全部成就、完成时间和成就原始字段）",
            "Liyin 格式",
            "实验性 UIAF v1.2（非官方，按绝区零提案结构）",
        };
        var selected = ConsoleSelectionMenu.Read(options, 0, cancellationToken);
        Console.WriteLine($"已选择：{options[selected]}");

        var target = (ExportTarget)selected;
        if (target == ExportTarget.UiafExperimental)
        {
            Console.WriteLine(
                "提示：现行 UIAF（v1.1）尚未正式定义此结构；"
                    + "该文件按待讨论提案生成，不保证与任何第三方工具兼容。"
            );
        }

        return target;
    }
}

internal enum ExportTarget
{
    AchievementBackup,
    Liyin,
    UiafExperimental,
}
