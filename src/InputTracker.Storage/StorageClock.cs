using System.Diagnostics;

namespace InputTracker.Storage;

public interface IStorageClock
{
    long Frequency { get; }
    long GetTimestamp();
    DateTimeOffset GetUtcNow();
    string GetTimeZoneId();
}

public sealed class SystemStorageClock : IStorageClock
{
    public static SystemStorageClock Instance { get; } = new();

    public long Frequency => Stopwatch.Frequency;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
    public string GetTimeZoneId() => TimeZoneInfo.Local.Id;
}

public readonly record struct TimeCalibrationSegment(
    Guid SessionId,
    long Frequency,
    long MonotonicAnchor,
    long UtcAnchorMicroseconds,
    long SamplingUncertaintyTicks,
    string TimeZoneId)
{
    private const long UnixEpochTicks = 621355968000000000L;

    public static TimeCalibrationSegment Capture(Guid sessionId, IStorageClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (clock.Frequency <= 0)
        {
            throw new InvalidOperationException("The monotonic clock frequency must be positive.");
        }

        long before = clock.GetTimestamp();
        DateTimeOffset utc = clock.GetUtcNow();
        long after = clock.GetTimestamp();
        if (after < before)
        {
            throw new InvalidOperationException("The monotonic clock moved backwards while calibrating.");
        }

        long anchor = before + ((after - before) / 2);
        long utcMicroseconds = checked((utc.UtcDateTime.Ticks - UnixEpochTicks) / 10);
        return new TimeCalibrationSegment(
            sessionId,
            clock.Frequency,
            anchor,
            utcMicroseconds,
            after - before,
            clock.GetTimeZoneId());
    }

    public long ToUtcMicroseconds(long monotonicTimestamp)
    {
        Int128 scaled = (Int128)(monotonicTimestamp - MonotonicAnchor) * 1_000_000;
        return checked(UtcAnchorMicroseconds + (long)(scaled / Frequency));
    }
}
