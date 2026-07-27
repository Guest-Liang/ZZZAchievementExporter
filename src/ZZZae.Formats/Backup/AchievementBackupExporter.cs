using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZae.Core.Achievements;

namespace ZZZae.Formats.Backup;

public static class AchievementBackupExporter
{
    private static readonly TimeSpan ChinaStandardOffset = TimeSpan.FromHours(8);

    public static string Serialize(AchievementSnapshot snapshot, string metadataVersion, int metadataCount)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var document = new AchievementBackupDocument
        {
            Schema = "ZZZae.AchievementBackup",
            SchemaVersion = 1,
            ExportApp = "ZZZae",
            CapturedAt = snapshot.CapturedAt.ToOffset(ChinaStandardOffset),
            GameVersion = snapshot.GameVersion,
            MetadataVersion = metadataVersion,
            MetadataCount = metadataCount,
            Detection = new DetectionInfo
            {
                CommandId = snapshot.SourceCommandId,
                RecordFieldPath = snapshot.RecordFieldPath,
                IdFieldNumber = snapshot.IdFieldNumber,
                FinishTimestampFieldNumber = snapshot.FinishTimestampFieldNumber,
                CompletedFlagFieldNumber = snapshot.CompletedFlagFieldNumber,
                CatalogMatchCount = snapshot.CatalogMatchCount,
                UnknownIdCount = snapshot.UnknownIdCount,
            },
            Records = snapshot
                .Records.Select(static record => new AchievementBackupRecord
                {
                    Id = record.Id,
                    IsCompleted = record.IsCompleted,
                    FinishTimestamp = record.FinishTimestamp,
                    FinishTimeUtc8 = AchievementTimestamp
                        .Normalize(record.FinishTimestamp)
                        ?.ToOffset(ChinaStandardOffset),
                    CompletedFlag = record.CompletedFlag,
                    RawVarints = record.RawVarints.ToDictionary(
                        static pair => pair.Key.ToString(CultureInfo.InvariantCulture),
                        static pair => pair.Value
                    ),
                })
                .ToArray(),
        };

        return JsonSerializer.Serialize(
            document,
            AchievementBackupJsonContext.Default.AchievementBackupDocument
        );
    }
}

internal sealed class AchievementBackupDocument
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("schema_version")]
    public required int SchemaVersion { get; init; }

    [JsonPropertyName("export_app")]
    public required string ExportApp { get; init; }

    [JsonPropertyName("captured_at")]
    public required DateTimeOffset CapturedAt { get; init; }

    [JsonPropertyName("game_version")]
    public required string GameVersion { get; init; }

    [JsonPropertyName("metadata_version")]
    public required string MetadataVersion { get; init; }

    [JsonPropertyName("metadata_count")]
    public required int MetadataCount { get; init; }

    [JsonPropertyName("detection")]
    public required DetectionInfo Detection { get; init; }

    [JsonPropertyName("records")]
    public required AchievementBackupRecord[] Records { get; init; }
}

internal sealed class DetectionInfo
{
    [JsonPropertyName("command_id")]
    public required uint CommandId { get; init; }

    [JsonPropertyName("record_field_path")]
    public required string RecordFieldPath { get; init; }

    [JsonPropertyName("id_field_number")]
    public required uint IdFieldNumber { get; init; }

    [JsonPropertyName("finish_timestamp_field_number")]
    public required uint FinishTimestampFieldNumber { get; init; }

    [JsonPropertyName("completed_flag_field_number")]
    public required uint CompletedFlagFieldNumber { get; init; }

    [JsonPropertyName("catalog_match_count")]
    public required int CatalogMatchCount { get; init; }

    [JsonPropertyName("unknown_id_count")]
    public required int UnknownIdCount { get; init; }
}

internal sealed class AchievementBackupRecord
{
    [JsonPropertyName("id")]
    public required uint Id { get; init; }

    [JsonPropertyName("is_completed")]
    public required bool IsCompleted { get; init; }

    [JsonPropertyName("finish_timestamp")]
    public long? FinishTimestamp { get; init; }

    [JsonPropertyName("finish_time_utc8")]
    public DateTimeOffset? FinishTimeUtc8 { get; init; }

    [JsonPropertyName("completed_flag")]
    public bool? CompletedFlag { get; init; }

    [JsonPropertyName("raw_varints")]
    public required Dictionary<string, ulong> RawVarints { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AchievementBackupDocument))]
internal sealed partial class AchievementBackupJsonContext : JsonSerializerContext;
