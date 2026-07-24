namespace ZZZae.Core.Achievements;

public sealed record AchievementSnapshot
{
    public required DateTimeOffset CapturedAt { get; init; }

    public required string GameVersion { get; init; }

    public required uint SourceCommandId { get; init; }

    public required string RecordFieldPath { get; init; }

    public required uint IdFieldNumber { get; init; }

    public required uint FinishTimestampFieldNumber { get; init; }

    public required uint CompletedFlagFieldNumber { get; init; }

    public required int CatalogMatchCount { get; init; }

    public required int UnknownIdCount { get; init; }

    public required IReadOnlyList<AchievementRecord> Records { get; init; }

    public required byte[] RawHeader { get; init; }

    public required byte[] RawPayload { get; init; }
}
