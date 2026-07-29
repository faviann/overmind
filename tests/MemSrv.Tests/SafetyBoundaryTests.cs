using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
[Collection("database")]
public sealed class SafetyBoundaryTests : HttpSeamTestBase
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

        string source = JsonSerializer.Serialize(new
        {
            safe = "kept",
            oversized
        });
        var result = gate.ScanJson(source);
        using JsonDocument document = JsonDocument.Parse(result.Redacted);
        Assert.Equal("kept", document.RootElement.GetProperty("safe").GetString());
        Assert.Equal(
            "[OMITTED:leaf_exceeds_limit]",
            document.RootElement.GetProperty("oversized").GetString());
        Assert.Equal(["leaf_exceeds_limit"], result.OmissionReasons);

        // A required identity value that large cannot be inspected at all.
        Assert.Throws<SafetyScanException>(() => gate.AssertAllowed(oversized));
        ReleaseLargeValues();
    }

    [Fact]
    public async Task ObservationAtTheDocumented128MiBLimitIsAcceptedAndBeyondItIsWhollyOmitted()
    {
        Assert.Equal(ObservationLimitBytes, SafetyBudgets.Default.MaxObservationBytes);
        var gate = new NeverStoreGate(_shippedRules);
        string credential = $"mcap_{Guid.NewGuid():N}";
        string bindingName = $"content-boundary-{Guid.NewGuid():N}";
        await new CaptureEnrollment(RuntimeConnection, gate).EnrollAsync(
            bindingName,
            "codex",
            $"capture:{bindingName}",
            credential);
        CaptureBindingContext binding =
            Assert.IsType<CaptureBindingContext>(
                await new CaptureAuthority(RuntimeConnection).ResolveAsync(credential));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        static CaptureObservationCommand Command(
            int payloadLength,
            long position,
            string locator)
        {
            JsonElement sourcePayload = JsonSerializer.SerializeToElement(new
            {
                value = new string('x', payloadLength)
            });
            return CaptureObservationCommand.FromRequest(new CaptureObservationRequest(
                1,
                "content-boundary-stream",
                position,
                new CaptureLocator("native_id", locator, null, null, null),
                null,
                new CaptureSource("codex", "synthetic", "boundary"),
                new CaptureAdapter("boundary-test", "1"),
                sourcePayload,
                [
                    new CaptureEvent(
                        "boundary/0",
                        0,
                        "opaque",
                        "harness",
                        JsonSerializer.SerializeToElement(new { safe = "kept" }),
                        null,
                        [])
                ]));
        }

        CaptureObservationCommand emptyAtLimit = Command(0, 0, "limit0");
        int atLimitOverhead = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(emptyAtLimit, options));
        CaptureObservationCommand atLimit = Command(
            checked((int)(ObservationLimitBytes - atLimitOverhead)),
            0,
            "limit0");
        Assert.Equal(
            ObservationLimitBytes,
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(atLimit, options)));

        CaptureImportReceipt accepted = await new CaptureIngestion(
            RuntimeConnection, gate).ImportAsync(binding, atLimit);
        Assert.Equal("new", accepted.Status);
        Assert.Contains(
            "omission:leaf_exceeds_limit",
            accepted.Observation.Scan.RuleIds);
        emptyAtLimit = null!;
        atLimit = null!;
        ReleaseLargeValues();

        CaptureObservationCommand emptyOverLimit = Command(0, 1, "limit1");
        int overLimitOverhead = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(emptyOverLimit, options));
        CaptureObservationCommand overLimit = Command(
            checked((int)(ObservationLimitBytes + 1 - overLimitOverhead)),
            1,
            "limit1");
        Assert.Equal(
            ObservationLimitBytes + 1,
            Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(overLimit, options)));

        CaptureImportReceipt omitted = await new CaptureIngestion(
            RuntimeConnection, gate).ImportAsync(binding, overLimit);
        Assert.Equal("new", omitted.Status);
        Assert.Equal(
            "observation_exceeds_content_limit",
            omitted.Observation.SafeSourcePayload.GetProperty("omission")
                .GetProperty("reason").GetString());
        Assert.Equal(
            ["observation/omitted"],
            omitted.Events.Select(item => item.Event.PartKey));
        emptyOverLimit = null!;
        overLimit = null!;
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
