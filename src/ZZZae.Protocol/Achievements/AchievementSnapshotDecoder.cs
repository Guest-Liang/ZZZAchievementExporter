using System.Globalization;
using ZZZae.Core.Achievements;
using ZZZae.Core.Profiles;
using ZZZae.Protocol.Capture;
using ZZZae.Protocol.Metadata;
using ZZZae.Protocol.Protobuf;

namespace ZZZae.Protocol.Achievements;

public sealed class AchievementSnapshotDecoder
{
    private readonly AchievementCatalog _catalog;
    private readonly string _gameVersion;
    private readonly AchievementProtocolProfile _profile;
    private readonly uint[] _recordPath;

    public AchievementSnapshotDecoder(
        AchievementCatalog catalog,
        string gameVersion,
        AchievementProtocolProfile profile
    )
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _gameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "unknown" : gameVersion;
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _recordPath = ParseRecordPath(profile.RecordFieldPath);
    }

    public bool TryDecode(CapturedPacket packet, out AchievementSnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(packet);
        snapshot = null;

        if (
            packet.CommandId != _profile.FullSnapshotCommandId
            || packet.Body.Length < 16
            || !ProtoWire.TryParse(packet.Body, out var root)
            || root is null
        )
        {
            return false;
        }

        var rows = ReadRows(root, _recordPath);
        if (!HasVerifiedIdShape(rows))
        {
            return false;
        }

        var records = BuildRecords(rows, packet.CapturedAt);
        if (records is null || records.Count < 3)
        {
            return false;
        }

        var catalogMatches = records.Count(record => _catalog.Ids.Contains(record.Id));
        snapshot = new AchievementSnapshot
        {
            CapturedAt = packet.CapturedAt,
            GameVersion = _gameVersion,
            SourceCommandId = packet.CommandId,
            RecordFieldPath = _profile.RecordFieldPath,
            IdFieldNumber = _profile.IdFieldNumber,
            FinishTimestampFieldNumber = _profile.FinishTimestampFieldNumber,
            CompletedFlagFieldNumber = _profile.CompletedFlagFieldNumber,
            CatalogMatchCount = catalogMatches,
            UnknownIdCount = records.Count - catalogMatches,
            Records = records,
            RawHeader = packet.Header.ToArray(),
            RawPayload = packet.Body.ToArray(),
        };
        return true;
    }

    private bool HasVerifiedIdShape(IReadOnlyList<Dictionary<uint, ulong>> rows)
    {
        var values = rows.Where(row => row.ContainsKey(_profile.IdFieldNumber))
            .Select(row => row[_profile.IdFieldNumber])
            .ToArray();
        if (values.Length < 3)
        {
            return false;
        }

        var known = values.Count(value => value <= uint.MaxValue && _catalog.Ids.Contains((uint)value));
        var plausible = values.Count(value => value <= uint.MaxValue && LooksLikeAchievementId((uint)value));

        return known >= 3 && known * 5 >= values.Length * 3 && plausible * 10 >= values.Length * 9;
    }

    private IReadOnlyList<AchievementRecord>? BuildRecords(
        IReadOnlyList<Dictionary<uint, ulong>> rows,
        DateTimeOffset capturedAt
    )
    {
        var byId = new Dictionary<uint, AchievementRecord>();

        foreach (var row in rows)
        {
            if (!row.TryGetValue(_profile.IdFieldNumber, out var rawId) || rawId > uint.MaxValue)
            {
                continue;
            }

            var id = (uint)rawId;
            if (!LooksLikeAchievementId(id))
            {
                continue;
            }

            var finishTimestamp = ReadInt64(row, _profile.FinishTimestampFieldNumber);
            if (finishTimestamp is > 0 && !IsPlausibleTimestamp(finishTimestamp.Value, capturedAt))
            {
                return null;
            }

            var completedFlag = ReadBoolean(row, _profile.CompletedFlagFieldNumber);
            var record = new AchievementRecord
            {
                Id = id,
                IsCompleted = finishTimestamp is > 0 || completedFlag is true,
                FinishTimestamp = finishTimestamp,
                CompletedFlag = completedFlag,
                RawVarints = new Dictionary<uint, ulong>(row),
            };

            if (!byId.TryGetValue(id, out var previous) || Prefer(record, previous))
            {
                byId[id] = record;
            }
        }

        return byId.Values.OrderBy(static record => record.Id).ToArray();
    }

    private static IReadOnlyList<Dictionary<uint, ulong>> ReadRows(ProtoMessage root, IReadOnlyList<uint> path)
    {
        var containers = new List<ProtoMessage> { root };

        for (var index = 0; index < path.Count - 1; index++)
        {
            var next = new List<ProtoMessage>();
            foreach (var container in containers)
            {
                foreach (var field in container.Fields)
                {
                    if (
                        field.Number != path[index]
                        || field.WireType != ProtoWireType.LengthDelimited
                        || !ProtoWire.TryParse(field.Bytes, out var child)
                        || child is null
                    )
                    {
                        continue;
                    }

                    next.Add(child);
                }
            }

            if (next.Count == 0)
            {
                return [];
            }

            containers = next;
        }

        var rows = new List<Dictionary<uint, ulong>>();
        var recordFieldNumber = path[^1];
        foreach (var container in containers)
        {
            foreach (var field in container.Fields)
            {
                if (
                    field.Number != recordFieldNumber
                    || field.WireType != ProtoWireType.LengthDelimited
                    || !ProtoWire.TryParse(field.Bytes, out var record)
                    || record is null
                    || !TryCreateVarintRow(record, out var row)
                )
                {
                    continue;
                }

                rows.Add(row);
            }
        }

        return rows;
    }

    private static bool TryCreateVarintRow(ProtoMessage message, out Dictionary<uint, ulong> row)
    {
        row = new Dictionary<uint, ulong>();
        if (message.Fields.Count is < 1 or > 32)
        {
            return false;
        }

        foreach (var field in message.Fields)
        {
            if (field.WireType != ProtoWireType.Varint)
            {
                continue;
            }

            if (!row.TryAdd(field.Number, field.Varint))
            {
                row.Clear();
                return false;
            }
        }

        return row.Count != 0;
    }

    private static bool Prefer(AchievementRecord candidate, AchievementRecord previous)
    {
        if (candidate.IsCompleted != previous.IsCompleted)
        {
            return candidate.IsCompleted;
        }

        return candidate.RawVarints.Count > previous.RawVarints.Count;
    }

    private static long? ReadInt64(IReadOnlyDictionary<uint, ulong> row, uint fieldNumber)
    {
        return row.TryGetValue(fieldNumber, out var value) && value <= long.MaxValue ? (long)value : null;
    }

    private static bool? ReadBoolean(IReadOnlyDictionary<uint, ulong> row, uint fieldNumber)
    {
        return row.TryGetValue(fieldNumber, out var value) && value <= 1 ? value == 1 : null;
    }

    private static bool IsPlausibleTimestamp(long value, DateTimeOffset capturedAt)
    {
        const long earliestSeconds = 1_262_304_000;
        var latestSeconds = capturedAt.AddYears(5).ToUnixTimeSeconds();

        return value >= earliestSeconds && value <= latestSeconds
            || value >= earliestSeconds * 1_000 && value <= latestSeconds * 1_000
            || value >= earliestSeconds * 1_000_000 && value <= latestSeconds * 1_000_000;
    }

    private static bool LooksLikeAchievementId(uint value)
    {
        return value is >= 1_000_000 and <= 9_999_999;
    }

    private static uint[] ParseRecordPath(string path)
    {
        if (
            string.IsNullOrWhiteSpace(path)
            || !path.StartsWith("$.", StringComparison.Ordinal)
            || !path.EndsWith("[]", StringComparison.Ordinal)
        )
        {
            throw new ArgumentException("成就记录路径必须采用 $.字段.字段[] 格式。", nameof(path));
        }

        var segments = path[2..^2].Split('.', StringSplitOptions.RemoveEmptyEntries);
        var result = new uint[segments.Length];
        if (result.Length == 0)
        {
            throw new ArgumentException("成就记录路径不能为空。", nameof(path));
        }

        for (var index = 0; index < segments.Length; index++)
        {
            if (
                !uint.TryParse(segments[index], NumberStyles.None, CultureInfo.InvariantCulture, out result[index])
                || result[index] == 0
            )
            {
                throw new ArgumentException($"成就记录路径包含无效字段：{segments[index]}。", nameof(path));
            }
        }

        return result;
    }
}
