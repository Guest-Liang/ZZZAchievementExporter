namespace ZZZae.Formats;

internal static class AchievementTimestamp
{
    public static DateTimeOffset? Normalize(long? raw)
    {
        if (raw is null or <= 0)
        {
            return null;
        }

        try
        {
            return raw.Value switch
            {
                >= 1_000_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(raw.Value / 1_000),
                >= 1_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(raw.Value),
                >= 1_000_000_000 => DateTimeOffset.FromUnixTimeSeconds(raw.Value),
                _ => null,
            };
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
