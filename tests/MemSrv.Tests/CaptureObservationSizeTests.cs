using System.Text;
using System.Text.Json;
using MemSrv.Core;

namespace MemSrv.Tests;

// Capture alone owns the documented whole-observation fidelity ceiling.
[Collection("database")]
public sealed class CaptureObservationSizeTests : HttpSeamTestBase
{
    private const long ObservationLimitBytes = 128L * 1024 * 1024;

    private readonly string _shippedRules =
        Path.Combine(TestProcessRunner.RepoRoot, "config/never_store.yaml");

    [Fact]
    public async Task ObservationAtTheDocumented128MiBLimitIsAcceptedAndBeyondItIsWhollyOmitted()
    {
        Assert.Equal(ObservationLimitBytes, CaptureFidelityPolicy.ProductionContentBytes);
        var gate = new WriteSafetyGate(_shippedRules);
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
        CaptureOutcomeCounter leafOutcome = Assert.Single(accepted.Outcome.Counters);
        Assert.Equal(CaptureOutcomeReason.LeafExceedsLimit, leafOutcome.Reason);
        Assert.Equal(CaptureSizeBand.Over64MiBThrough128MiB, leafOutcome.SizeBand);
        Assert.Equal(1, leafOutcome.Count);
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
            RuntimeConnection,
            gate,
            CaptureFidelityPolicy.ProductionContentBytes + 1).ImportAsync(binding, overLimit);
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
