using CaptureAdapters;
using MemSrv.Core;

namespace MemSrv.Tests;

public sealed class CaptureScheduleTests
{
    [Theory]
    [InlineData("80", "40", 80, 40)]
    [InlineData("150", "60", 150, 60)]
    public void ProductionConfigurationBindsBothNamedCadenceInputs(
        string interval,
        string jitter,
        int expectedInterval,
        int expectedJitter)
    {
        var values = new Dictionary<string, string>
        {
            ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = interval,
            ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = jitter
        };

        CaptureRescanSchedule schedule =
            CaptureRescanConfiguration.Load(name => values.GetValueOrDefault(name));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedInterval), schedule.Interval);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedJitter), schedule.MaximumJitter);
    }

    [Theory]
    [InlineData("0", "0", "OVERMIND_CAPTURE_SCAN_INTERVAL_MS")]
    [InlineData("10", "-1", "OVERMIND_CAPTURE_SCAN_JITTER_MS")]
    public async Task PackagedTracerFailsFastForInvalidNamedCadenceInput(
        string interval,
        string jitter,
        string invalidName)
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-invalid-schedule-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var result = await TestProcessRunner.RunCaptureTracerToExitAsync(
                new Dictionary<string, string>
                {
                    ["OVERMIND_CODEX_CAPTURE_ENABLE"] = "synthetic-non-production",
                    ["OVERMIND_CAPTURE_URL"] = "http://127.0.0.1:1",
                    ["OVERMIND_CAPTURE_CREDENTIAL"] = $"mcap_{Guid.NewGuid():N}",
                    ["OVERMIND_CODEX_TRANSCRIPT_ROOT"] = root,
                    ["OVERMIND_CAPTURE_STATE_DIR"] = Path.Combine(root, "state"),
                    ["OVERMIND_CAPTURE_SCAN_INTERVAL_MS"] = interval,
                    ["OVERMIND_CAPTURE_SCAN_JITTER_MS"] = jitter
                });

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Contains(invalidName, result.Stderr, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisappearingStreamDoesNotStopLaterStreamsOrTheNextCycle()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-scan-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string firstPath = Path.Combine(root, "first.jsonl");
        string laterPath = Path.Combine(root, "later.jsonl");
        await File.WriteAllTextAsync(firstPath, "{}\n");
        await File.WriteAllTextAsync(laterPath, "{}\n");
        IReadOnlyList<CodexTranscriptStream> enumerated =
            CodexTranscriptDiscovery.Enumerate(root);
        File.Delete(firstPath);
        var completed = new List<string>();
        var failures = new List<Exception>();
        var state = new FileCaptureRuntimeState(Path.Combine(root, "state"));
        var adapter = new CodexJsonlAdapter();
        var safetyGate = new NeverStoreGate(Path.Combine(
            TestProcessRunner.RepoRoot, "config/never_store.yaml"));

        try
        {
            await CodexTranscriptScanCycle.RunAsync(
                enumerated,
                async (stream, token) =>
                {
                    await CodexCaptureClaimer.ClaimCompletedAsync(
                        adapter,
                        stream.Path,
                        stream.SourceStream,
                        state,
                        safetyGate,
                        token);
                    completed.Add(stream.Path);
                },
                failures.Add);

            Assert.Equal([laterPath], completed);
            Assert.IsType<FileNotFoundException>(Assert.Single(failures));
            CaptureRuntimeStreamState retained =
                Assert.Single((await state.ReadAsync()).Streams);
            Assert.Equal(enumerated[1].SourceStream, retained.SourceStream);
            Assert.Equal(0, retained.EnqueuedThrough);

            await File.WriteAllTextAsync(firstPath, "{}\n");
            completed.Clear();
            await CodexTranscriptScanCycle.RunAsync(
                CodexTranscriptDiscovery.Enumerate(root),
                async (stream, token) =>
                {
                    await CodexCaptureClaimer.ClaimCompletedAsync(
                        adapter,
                        stream.Path,
                        stream.SourceStream,
                        state,
                        safetyGate,
                        token);
                    completed.Add(stream.Path);
                },
                failures.Add);

            Assert.Equal([firstPath, laterPath], completed);
            Assert.Single(failures);
            Assert.Equal(2, (await state.ReadAsync()).Streams.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EachEnumerationSeesEveryCurrentConfiguredJsonlStream()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-discovery-{Guid.NewGuid():N}");
        string nested = Path.Combine(root, "nested");
        Directory.CreateDirectory(nested);
        string firstPath = Path.Combine(root, "first.jsonl");
        string secondPath = Path.Combine(nested, "second.jsonl");
        File.WriteAllText(firstPath, "{}\n");

        try
        {
            CodexTranscriptStream first =
                Assert.Single(CodexTranscriptDiscovery.Enumerate(root));

            File.WriteAllText(secondPath, "{}\n");
            IReadOnlyList<CodexTranscriptStream> rescanned =
                CodexTranscriptDiscovery.Enumerate(root);

            Assert.Equal(2, rescanned.Count);
            Assert.Contains(rescanned, stream => stream.Path == Path.GetFullPath(firstPath));
            Assert.Contains(rescanned, stream => stream.Path == Path.GetFullPath(secondPath));
            Assert.Equal(
                first.SourceStream,
                Assert.Single(rescanned, stream => stream.Path == Path.GetFullPath(firstPath))
                    .SourceStream);
            Assert.Equal(2, rescanned.Select(stream => stream.SourceStream).Distinct().Count());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MovingAConfiguredSessionIntoTheArchivePreservesIdentityAndProvesTerminality()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-archive-discovery-{Guid.NewGuid():N}");
        string sessions = Path.Combine(root, "sessions", "2026", "07");
        string archive = Path.Combine(root, "archived_sessions");
        Directory.CreateDirectory(sessions);
        Directory.CreateDirectory(archive);
        string activePath = Path.Combine(sessions, "session.jsonl");
        string archivedPath = Path.Combine(archive, "session.jsonl");
        File.WriteAllText(activePath, "{}");

        try
        {
            CodexTranscriptStream active =
                Assert.Single(CodexTranscriptDiscovery.Enumerate(root));
            Assert.False(active.TerminalAtEndOfFile);

            File.Move(activePath, archivedPath);
            CodexTranscriptStream archived =
                Assert.Single(CodexTranscriptDiscovery.Enumerate(root));

            Assert.True(archived.TerminalAtEndOfFile);
            Assert.Equal(active.SourceStream, archived.SourceStream);
            Assert.Equal(active.TranscriptIdentity, archived.TranscriptIdentity);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoveryRejectsAmbiguousOrSimultaneousSessionBasenames()
    {
        string root = Path.Combine(
            Path.GetTempPath(), $"capture-archive-ambiguity-{Guid.NewGuid():N}");
        string firstActive = Path.Combine(root, "sessions", "2026", "07", "session.jsonl");
        string secondActive = Path.Combine(root, "sessions", "2026", "08", "session.jsonl");
        string archived = Path.Combine(root, "archived_sessions", "session.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(firstActive)!);
        Directory.CreateDirectory(Path.GetDirectoryName(secondActive)!);
        Directory.CreateDirectory(Path.GetDirectoryName(archived)!);
        File.WriteAllText(firstActive, "{}");
        File.WriteAllText(secondActive, "{}");

        try
        {
            Assert.Throws<InvalidDataException>(() =>
                CodexTranscriptDiscovery.Enumerate(root));

            File.Delete(secondActive);
            File.WriteAllText(archived, "{}");
            Assert.Throws<InvalidDataException>(() =>
                CodexTranscriptDiscovery.Enumerate(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(80, 40, 0.25, 90)]
    [InlineData(150, 60, 0.75, 195)]
    public async Task StartupCompletesBeforeFreshJitteredNonOverlappingRescans(
        int intervalMilliseconds,
        int jitterMilliseconds,
        double jitterSample,
        int expectedDelayMilliseconds)
    {
        var startupEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var twoDelaysObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int activeCycles = 0;
        int maximumActiveCycles = 0;
        int cycleCount = 0;
        var samples = new Queue<double>([jitterSample, 1 - jitterSample]);
        var delays = new List<TimeSpan>();

        Task loop = CaptureRescanScheduler.RunAsync(
            async token =>
            {
                int active = Interlocked.Increment(ref activeCycles);
                maximumActiveCycles = Math.Max(maximumActiveCycles, active);
                int cycle = Interlocked.Increment(ref cycleCount);
                if (cycle == 1)
                {
                    startupEntered.SetResult();
                    await releaseStartup.Task.WaitAsync(token);
                }
                Interlocked.Decrement(ref activeCycles);
                if (cycle == 3)
                {
                    cancellation.Cancel();
                }
            },
            new CaptureRescanSchedule(
                TimeSpan.FromMilliseconds(intervalMilliseconds),
                TimeSpan.FromMilliseconds(jitterMilliseconds)),
            () => samples.Dequeue(),
            (delay, _) =>
            {
                Assert.Equal(0, Volatile.Read(ref activeCycles));
                delays.Add(delay);
                if (delays.Count == 2)
                {
                    twoDelaysObserved.TrySetResult();
                }
                return Task.CompletedTask;
            },
            cancellation.Token);

        await startupEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(twoDelaysObserved.Task.IsCompleted);
        releaseStartup.SetResult();

        await twoDelaysObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop;
        Assert.Equal(
            [
                TimeSpan.FromMilliseconds(expectedDelayMilliseconds),
                TimeSpan.FromMilliseconds(
                    intervalMilliseconds + jitterMilliseconds * (1 - jitterSample))
            ],
            delays);
        Assert.Equal(3, cycleCount);
        Assert.Equal(1, maximumActiveCycles);
    }
}
