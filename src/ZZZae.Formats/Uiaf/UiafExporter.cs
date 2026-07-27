using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZae.Core.Achievements;

namespace ZZZae.Formats.Uiaf;

public static class UiafExporter
{
    private const uint ProgressFieldNumber = 2;
    private const uint CompletedValueFieldNumber = 5;
    private const long UnknownCompletionTimestamp = 253_402_271_999;
    private const uint UnfinishedStatus = 1;
    private const uint FinishedStatus = 2;

    public static string Serialize(AchievementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var document = new UiafDocument
        {
            Info = new UiafInfo
            {
                ExportApp = "ZZZae",
                UiafVersion = "v1.2",
                ExportTimestamp = snapshot.CapturedAt.ToUnixTimeSeconds(),
            },
            Nap = new UiafNapData
            {
                List = snapshot
                    .Records.OrderBy(static record => record.Id)
                    .Select(static record => new UiafNapAchievement
                    {
                        Id = record.Id,
                        Current = ReadCurrent(record),
                        Status = record.IsCompleted ? FinishedStatus : UnfinishedStatus,
                        Timestamp =
                            AchievementTimestamp
                                .Normalize(record.FinishTimestamp)
                                ?.ToUnixTimeSeconds()
                            ?? UnknownCompletionTimestamp,
                    })
                    .ToArray(),
            },
        };

        return JsonSerializer.Serialize(document, UiafJsonContext.Default.UiafDocument);
    }

    private static ulong ReadCurrent(AchievementRecord record)
    {
        if (record.RawVarints.TryGetValue(CompletedValueFieldNumber, out var completedValue))
        {
            return completedValue;
        }

        return record.RawVarints.TryGetValue(ProgressFieldNumber, out var progress) ? progress : 0;
    }
}

internal sealed class UiafDocument
{
    [JsonPropertyName("info")]
    public required UiafInfo Info { get; init; }

    [JsonPropertyName("nap")]
    public required UiafNapData Nap { get; init; }
}

internal sealed class UiafInfo
{
    [JsonPropertyName("export_timestamp")]
    public required long ExportTimestamp { get; init; }

    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("uiaf_version")]
    public required string UiafVersion { get; init; }
}

internal sealed class UiafNapData
{
    [JsonPropertyName("list")]
    public required UiafNapAchievement[] List { get; init; }
}

internal sealed class UiafNapAchievement
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("current")]
    public required ulong Current { get; init; }

    [JsonPropertyName("status")]
    public required uint Status { get; init; }

    [JsonPropertyName("timestamp")]
    public required long Timestamp { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(UiafDocument))]
internal sealed partial class UiafJsonContext : JsonSerializerContext;
