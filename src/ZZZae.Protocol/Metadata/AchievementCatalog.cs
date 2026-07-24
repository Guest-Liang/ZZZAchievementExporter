using System.Globalization;
using System.Text.Json;

namespace ZZZae.Protocol.Metadata;

public sealed record AchievementCatalog
{
    private const string ResourceName =
        "ZZZae.Metadata.AchievementInfo.json";

    public required IReadOnlySet<uint> Ids { get; init; }

    public required string LatestVersion { get; init; }

    public int Count => Ids.Count;

    public static AchievementCatalog LoadBundled()
    {
        var assembly = typeof(AchievementCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"缺少内嵌成就元数据资源：{ResourceName}");
        using var document = JsonDocument.Parse(stream);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "内嵌 AchievementInfo.json 的根节点不是对象。");
        }

        var ids = new HashSet<uint>();
        Version? latestVersion = null;
        var latestVersionText = "unknown";

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!uint.TryParse(
                    property.Name,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var id))
            {
                continue;
            }

            ids.Add(id);

            if (property.Value.ValueKind != JsonValueKind.Object
                || !property.Value.TryGetProperty(
                    "Version",
                    out var versionElement)
                || versionElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var versionText = versionElement.GetString();
            if (!Version.TryParse(versionText, out var version)
                || latestVersion is not null
                && version <= latestVersion)
            {
                continue;
            }

            latestVersion = version;
            latestVersionText = versionText!;
        }

        if (ids.Count == 0)
        {
            throw new InvalidDataException(
                "内嵌 AchievementInfo.json 中没有成就 ID。");
        }

        return new AchievementCatalog
        {
            Ids = ids,
            LatestVersion = latestVersionText
        };
    }
}
