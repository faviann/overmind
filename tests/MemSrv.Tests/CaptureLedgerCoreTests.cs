using Dapper;
using MemSrv.Core;
using Npgsql;
using System.Text.Json;

namespace MemSrv.Tests;

// Mechanical and module-seam coverage for inaccessible legacy capture residue.
// No packaged route or operator command participates in these tests.
[Collection("database")]
public sealed class CaptureLedgerCoreTests : IAsyncLifetime
{
    private readonly string _root = TestProcessRunner.RepoRoot;

    public Task InitializeAsync() => TestDatabase.PrepareClassDatabaseAsync(
        typeof(CaptureLedgerCoreTests), Path.Combine(_root, "migrations"));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RetryGapAndChangedContentPreserveTheContiguousCheckpoint()
    {
        (CaptureBindingContext binding, CaptureIngestion ingestion) = await HarnessAsync();
        string session = $"core-checkpoint-{Guid.NewGuid():N}";
        string firstLocator = $"first-{Guid.NewGuid():N}";

        CaptureImportReceipt first = await ingestion.ImportAsync(
            binding, Command(session, 0, firstLocator, "original"));
        CaptureImportReceipt retry = await ingestion.ImportAsync(
            binding, Command(session, 9, firstLocator, "original"));
        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(first.ObservationUuid, retry.ObservationUuid);
        Assert.Equal(0, retry.SourcePosition);

        CaptureConflictException gap = await Assert.ThrowsAsync<CaptureConflictException>(
            () => ingestion.ImportAsync(
                binding, Command(session, 2, $"gap-{Guid.NewGuid():N}", "gap")));
        Assert.Equal("blocked_by_earlier_gap", gap.Reason);

        CaptureConflictException changed = await Assert.ThrowsAsync<CaptureConflictException>(
            () => ingestion.ImportAsync(
                binding, Command(session, 0, firstLocator, "changed")));
        Assert.Equal("accepted_source_conflict", changed.Reason);

        CaptureImportReceipt next = await ingestion.ImportAsync(
            binding, Command(session, 1, $"next-{Guid.NewGuid():N}", "next"));
        Assert.Equal("new", next.Status);
        Assert.Equal(1, next.SourcePosition);

        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnection);
        await connection.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT checkpoint_position FROM capture_source_streams WHERE stream_uuid = @streamUuid",
            new { streamUuid = first.Observation.SourceStreamUuid }));
    }

    [Fact]
    public async Task InvalidEventFanoutAppendsNothingAndDoesNotAdvanceCheckpoint()
    {
        (CaptureBindingContext binding, CaptureIngestion ingestion) = await HarnessAsync();
        string session = $"core-atomic-{Guid.NewGuid():N}";
        CaptureImportReceipt first = await ingestion.ImportAsync(
            binding, Command(session, 0, $"atomic-0-{Guid.NewGuid():N}", "accepted"));
        CaptureObservationCommand invalid = Command(
            session,
            1,
            $"atomic-1-{Guid.NewGuid():N}",
            "rejected",
            events:
            [
                Event("duplicate", 0, "one"),
                Event("duplicate", 1, "two"),
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ingestion.ImportAsync(binding, invalid));

        await using var connection = new NpgsqlConnection(TestDatabase.AdminConnection);
        await connection.OpenAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM capture_observations WHERE stream_uuid = @streamUuid",
            new { streamUuid = first.Observation.SourceStreamUuid }));
        Assert.Equal(0, await connection.ExecuteScalarAsync<long>(
            "SELECT checkpoint_position FROM capture_source_streams WHERE stream_uuid = @streamUuid",
            new { streamUuid = first.Observation.SourceStreamUuid }));
    }

    [Fact]
    public async Task UnchangedDerivedRecordConvergesAcrossSupportedAdapterVersion()
    {
        (CaptureBindingContext binding, CaptureIngestion ingestion) = await HarnessAsync();
        string session = $"core-adapter-{Guid.NewGuid():N}";
        string locator = $"adapter-{Guid.NewGuid():N}";
        CaptureObservationCommand accepted = Command(
            session, 0, locator, "same record", adapterVersion: "8");
        CaptureObservationCommand upgraded = accepted with
        {
            SourcePosition = 7,
            Adapter = accepted.Adapter with { Version = "9" },
        };

        CaptureImportReceipt first = await ingestion.ImportAsync(binding, accepted);
        CaptureImportReceipt retry = await ingestion.ImportAsync(binding, upgraded);

        Assert.Equal("already_accepted", retry.Status);
        Assert.Equal(first.ObservationUuid, retry.ObservationUuid);
    }

    private async Task<(CaptureBindingContext Binding, CaptureIngestion Ingestion)> HarnessAsync()
    {
        string credential = $"mcap_{Guid.NewGuid():N}";
        string bindingName = $"core-{Guid.NewGuid():N}";
        var gate = new WriteSafetyGate(Path.Combine(_root, "config/never_store.yaml"));
        await new CaptureEnrollment(TestDatabase.RuntimeConnection, gate).EnrollAsync(
            bindingName,
            "codex",
            $"capture:{bindingName}",
            credential);
        CaptureBindingContext binding = Assert.IsType<CaptureBindingContext>(
            await new CaptureAuthority(TestDatabase.RuntimeConnection).ResolveAsync(credential));
        return (binding, new CaptureIngestion(TestDatabase.RuntimeConnection, gate));
    }

    private static CaptureObservationCommand Command(
        string session,
        long position,
        string locator,
        string text,
        string adapterVersion = "1",
        IReadOnlyList<CaptureEvent>? events = null) => new(
            1,
            new CaptureSourceIdentity(session),
            position,
            new CaptureSourceLocator.NativeId(locator),
            null,
            new CaptureSource("codex", "synthetic", "turn"),
            new CaptureAdapter("codex-synthetic-jsonl", adapterVersion),
            JsonSerializer.SerializeToElement(new { text }),
            events ?? [Event("message/0", 0, text)],
            null);

    private static CaptureEvent Event(string partKey, int order, string text) => new(
        partKey,
        order,
        "message",
        "user",
        JsonSerializer.SerializeToElement(new { text }),
        null,
        []);
}
