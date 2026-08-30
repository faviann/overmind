using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace MemSrv.Tests;

// The documented write-safety leaf limit, exercised at its real number rather
// than a convenient stand-in. Mechanism tests inject smaller budgets in
// WriteSafetyTests.
[Collection("database")]
public sealed class WriteSafetyBoundaryTests
{
    private const long LeafLimitBytes = 64L * 1024 * 1024;
    private const string FakeAwsKeyId = "AKIA" + "BOUNDARYFAKE0001";
    private readonly string _shippedRules =
        Path.Combine(TestProcessRunner.RepoRoot, "config/never_store.yaml");

    [Fact]
    public void LeafAtTheDocumented64MiBLimitIsScannedToItsFinalByte()
    {
        Assert.Equal(LeafLimitBytes, WriteSafetyBudgets.Default.MaxLeafBytes);
        var gate = new WriteSafetyGate(_shippedRules);
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
            clock.Elapsed < WriteSafetyBudgets.Default.MaxScanTime,
            $"A leaf at the documented limit took {clock.Elapsed.TotalSeconds:0.0}s, which " +
            $"exceeds the published {WriteSafetyBudgets.Default.MaxScanTime.TotalSeconds:0}s scan-time budget.");
        ReleaseLargeValues();
    }

    [Fact]
    public void LeafBeyondTheDocumented64MiBLimitIsWhollyOmittedWithSafeSiblingsKept()
    {
        var gate = new WriteSafetyGate(_shippedRules);
        string oversized = new string('x', (int)LeafLimitBytes + 1);
        Assert.Equal(LeafLimitBytes + 1, Encoding.UTF8.GetByteCount(oversized));

        string source = JsonSerializer.Serialize(new { safe = "kept", oversized });
        var result = gate.ScanJson(source);
        using JsonDocument document = JsonDocument.Parse(result.Redacted);
        Assert.Equal("kept", document.RootElement.GetProperty("safe").GetString());
        Assert.Equal(
            "[OMITTED:leaf_exceeds_limit]",
            document.RootElement.GetProperty("oversized").GetString());
        Assert.Equal(["leaf_exceeds_limit"], result.OmissionReasons);
        Assert.Equal(LeafLimitBytes + 1, Assert.Single(result.Omissions).OriginalByteCount);

        Assert.Throws<WriteSafetyScanException>(() => gate.AssertAllowed(oversized));
        ReleaseLargeValues();
    }

    private static void ReleaseLargeValues()
    {
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}
