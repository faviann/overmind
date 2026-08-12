namespace CaptureAdapters;

using System.Globalization;
using System.Threading.Channels;

/// <summary>
/// The configured lower bound and additional random delay between complete
/// transcript scan cycles.
/// </summary>
public sealed record CaptureRescanSchedule
{
    public CaptureRescanSchedule(TimeSpan interval, TimeSpan maximumJitter)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval), "The rescan interval must be positive.");
        }
        if (maximumJitter < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumJitter), "The maximum rescan jitter cannot be negative.");
        }
        Interval = interval;
        MaximumJitter = maximumJitter;
    }

    public TimeSpan Interval { get; }
    public TimeSpan MaximumJitter { get; }
}

/// <summary>
/// Binds the packaged tracer's named environment inputs to the scheduler's
/// production schedule.
/// </summary>
public static class CaptureRescanConfiguration
{
    public const string IntervalEnvironmentName =
        "OVERMIND_CAPTURE_SCAN_INTERVAL_MS";
    public const string MaximumJitterEnvironmentName =
        "OVERMIND_CAPTURE_SCAN_JITTER_MS";

    public static CaptureRescanSchedule Load(
        Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        return new CaptureRescanSchedule(
            TimeSpan.FromMilliseconds(Read(
                readEnvironment, IntervalEnvironmentName, 1_000, positive: true)),
            TimeSpan.FromMilliseconds(Read(
                readEnvironment, MaximumJitterEnvironmentName, 250, positive: false)));
    }

    private static int Read(
        Func<string, string?> readEnvironment,
        string name,
        int defaultValue,
        bool positive)
    {
        string? raw = readEnvironment(name);
        if (raw is null)
        {
            return defaultValue;
        }
        if (!int.TryParse(
                raw, NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            || value < 0
            || (positive && value == 0))
        {
            throw new InvalidOperationException(
                $"{name} must be {(positive ? "a positive" : "a non-negative")} integer.");
        }
        return value;
    }
}

/// <summary>A bounded, coalescing request for an early transcript scan.</summary>
public sealed class CaptureScanWakeup
{
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

    public void Request() => _requests.Writer.TryWrite(true);

    internal async Task WaitAsync(
        TimeSpan delay,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        if (_requests.Reader.TryRead(out _)) return;

        using var delayCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task scheduled = delayAsync(delay, delayCancellation.Token);
        Task<bool> wake = _requests.Reader.WaitToReadAsync(delayCancellation.Token).AsTask();
        Task completed = await Task.WhenAny(scheduled, wake);
        if (completed == wake && await wake)
        {
            _requests.Reader.TryRead(out _);
            delayCancellation.Cancel();
            try { await scheduled; } catch (OperationCanceledException) { }
            return;
        }
        delayCancellation.Cancel();
        try { await wake; } catch (OperationCanceledException) { }
        await scheduled;
    }
}

/// <summary>
/// Runs startup discovery immediately, then waits a freshly jittered configured
/// delay before each later cycle. A cycle is awaited in full before another
/// delay is scheduled, so enumeration, claiming, and delivery cannot overlap.
/// </summary>
public static class CaptureRescanScheduler
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> scanCycle,
        CaptureRescanSchedule schedule,
        Func<double>? nextJitterSample = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        CancellationToken cancellationToken = default,
        CaptureScanWakeup? wakeup = null)
    {
        ArgumentNullException.ThrowIfNull(scanCycle);
        ArgumentNullException.ThrowIfNull(schedule);
        nextJitterSample ??= Random.Shared.NextDouble;
        delayAsync ??= Task.Delay;

        while (!cancellationToken.IsCancellationRequested)
        {
            await scanCycle(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            double sample = nextJitterSample();
            if (double.IsNaN(sample) || sample < 0 || sample >= 1)
            {
                throw new InvalidOperationException(
                    "The rescan jitter source must return a value in [0, 1).");
            }
            TimeSpan jitter = TimeSpan.FromTicks(
                checked((long)(schedule.MaximumJitter.Ticks * sample)));
            if (wakeup is null)
            {
                await delayAsync(schedule.Interval + jitter, cancellationToken);
            }
            else
            {
                await wakeup.WaitAsync(
                    schedule.Interval + jitter, delayAsync, cancellationToken);
            }
        }
    }
}
