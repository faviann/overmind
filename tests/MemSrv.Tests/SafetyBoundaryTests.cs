using System.Diagnostics;
using System.Text;
using MemSrv.Core;

namespace MemSrv.Tests;

// The documented content limits, exercised at their real numbers rather than
// at a convenient stand-in. Both live in one class on purpose: xUnit runs the
// tests of a class sequentially, so only one multi-hundred-megabyte value is
// live at a time even though `make test` runs four shards concurrently.
//
// The pathological cases whose MECHANISM (not number) is under test — match
// floods, decoder-candidate floods, malformed encodings, matcher timeouts,
// decode exhaustion — run against explicitly injected smaller budgets in
// SafetyGateTests and CaptureSafetyTests, so the suite does not pay for the
// production number twelve times.
public sealed class SafetyBoundaryTests
{
    private const long LeafLimitBytes = 64L * 1024 * 1024;
    private const long ObservationLimitBytes = 128L * 1024 * 1024;
    private const string FakeAwsKeyId = "AKIA" + "BOUNDARYFAKE0001";

    private readonly string _shippedRules =
        Path.Combine(TestProcessRunner.RepoRoot, "config/never_store.yaml");

    [Fact]
    public void LeafAtTheDocumented64MiBLimitIsScannedToItsFinalByte()
    {
        Assert.Equal(LeafLimitBytes, SafetyBudgets.Default.MaxLeafBytes);
        var gate = new NeverStoreGate(_shippedRules);
        // ASCII, so one char is one UTF-8 byte: the value is exactly at the
        // documented limit, and the only credential sits at its very end.
        string leaf = new string('x', (int)LeafLimitBytes - FakeAwsKeyId.Length - 1)
            + " " + FakeAwsKeyId;
        Assert.Equal(LeafLimitBytes, Encoding.UTF8.GetByteCount(leaf));

        var clock = Stopwatch.StartNew();
        var result = gate.Scan(leaf);
        clock.Stop();

        Assert.Empty(result.OmissionReasons);
        Assert.Equal(1, result.RedactionCount);
        Assert.Equal(["aws-access-key-id"], result.RuleIds);
        Assert.EndsWith("[REDACTED:aws-access-key-id]", result.Redacted);
        Assert.True(
            clock.Elapsed < SafetyBudgets.Default.MaxScanTime,
            $"A leaf at the documented limit took {clock.Elapsed.TotalSeconds:0.0}s, which " +
            $"exceeds the published {SafetyBudgets.Default.MaxScanTime.TotalSeconds:0}s scan-time budget.");
        ReleaseLargeValues();
    }

    [Fact]
    public void LeafBeyondTheDocumented64MiBLimitIsWhollyOmittedWithSafeSiblingsKept()
    {
        var gate = new NeverStoreGate(_shippedRules);
        string oversized = new string('x', (int)LeafLimitBytes + 1);
        Assert.Equal(LeafLimitBytes + 1, Encoding.UTF8.GetByteCount(oversized));

        var result = gate.Scan(oversized);
        Assert.Equal("[OMITTED:leaf_exceeds_limit]", result.Redacted);
        Assert.Equal(["leaf_exceeds_limit"], result.OmissionReasons);

        // A required identity value that large cannot be inspected at all.
        Assert.Throws<SafetyScanException>(() => gate.AssertAllowed(oversized));
        ReleaseLargeValues();
    }

    [Fact]
    public void ObservationAtTheDocumented128MiBLimitIsAcceptedAndBeyondItFailsClosed()
    {
        Assert.Equal(ObservationLimitBytes, SafetyBudgets.Default.MaxObservationBytes);
        var gate = new NeverStoreGate(_shippedRules);

        // Three-byte UTF-8 characters keep the managed string a third of the
        // size while the measured UTF-8 length is exactly the real limit.
        const int wide = 3;
        int wideCount = (int)(ObservationLimitBytes / wide);
        int remainder = (int)(ObservationLimitBytes - ((long)wideCount * wide));
        string atLimit = new string('一', wideCount) + new string('a', remainder);
        Assert.Equal(ObservationLimitBytes, Encoding.UTF8.GetByteCount(atLimit));

        gate.AssertObservationWithinBudget(atLimit);

        var failure = Assert.Throws<SafetyScanException>(
            () => gate.AssertObservationWithinBudget(atLimit + "a"));
        Assert.Contains($"observation budget of {ObservationLimitBytes} bytes", failure.Message);
        ReleaseLargeValues();
    }

    // Peak-memory hygiene, not correctness: `make test` runs four concurrent
    // shards, so the multi-hundred-megabyte value of one boundary test must not
    // still be reachable while the next one allocates. No assertion depends on
    // this running, or on when it runs.
    private static void ReleaseLargeValues()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
