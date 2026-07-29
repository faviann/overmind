using Dapper;
using MemSrv.Core;
using Npgsql;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MemSrv.Tests;

// Each test owns a disposable database (create → migrate → break → drop),
// mirroring the homelab-iac disposable verify step. Databases are uniquely
// named, but the class shares the "database" collection with MemoryServiceTests
// so the NOLOGIN test — which toggles the cluster-wide memsrv role — never runs
// concurrently with a test that connects as memsrv.
[Collection("database")]
public sealed class SchemaVerifierTests
{
    private static string MaintenanceConnection => TestDatabase.MaintenanceConnection;
    private readonly string _root = TestProcessRunner.RepoRoot;

    [Fact]
    public async Task VerifyPassesOnFreshlyMigratedSchema()
    {
        // The one deliberate direct-API test: proves VerifyAsync reports no
        // failures on a clean schema. Broken states below assert through memctl.
        await WithDisposableDbAsync(async admin =>
        {
            var result = await SchemaVerifier.VerifyAsync(admin);
            Assert.True(result.Passed, "Expected a freshly migrated schema to pass: " + string.Join("; ", result.Failures));
            Assert.Empty(result.Failures);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaExitsZeroOnMigratedSchema()
    {
        await WithDisposableDbAsync(async admin =>
        {
            var (exitCode, stdout, stderr) = await RunVerifySchemaAsync(admin);
            Assert.True(exitCode == 0, $"Expected exit 0. stdout={stdout} stderr={stderr}");
            Assert.Contains("schema verification passed", stdout, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenAppendOnlyTriggerIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DROP TRIGGER traces_immutable ON traces");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("traces_immutable", stderr, StringComparison.Ordinal);
            Assert.Contains("append-only", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenCaptureLedgerTriggerIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DROP TRIGGER captured_events_immutable ON captured_events");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("captured_events_immutable", stderr, StringComparison.Ordinal);
            Assert.Contains("append-only", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvHasDeleteGrantOnTraces()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "GRANT DELETE ON traces TO memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("DELETE grant on 'public.traces'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvHasDeleteGrantOnNonTracesTable()
    {
        // Proves the no-DELETE check spans every public table, not just traces.
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "GRANT DELETE ON memories TO memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("DELETE grant on 'public.memories'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvCanUpdateTraces()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "GRANT UPDATE ON traces TO memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("must not have UPDATE on 'public.traces'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvCanUpdateCaptureBindingAuthority()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "GRANT UPDATE ON capture_source_bindings TO memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("capture_source_bindings.stable_name", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_bindings.credential_hash", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_bindings.content_signature_key", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_bindings.route_namespace", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvCanUpdateCaptureStreamAuthority()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "GRANT UPDATE ON capture_source_streams TO memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("capture_source_streams.binding_uuid", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_streams.source_session_id", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_streams.effective_namespace", stderr, StringComparison.Ordinal);
            Assert.Contains("capture_source_streams.route_basis", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenCaptureCheckpointColumnGrantIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(
                admin,
                "REVOKE UPDATE (checkpoint_position) ON capture_source_streams FROM memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains(
                "missing UPDATE on 'public.capture_source_streams.checkpoint_position'",
                stderr,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenBootstrapNamespaceIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DELETE FROM namespaces WHERE name = 'homelab'");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("Missing bootstrap namespace 'homelab'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenDefaultRetrievalConfigIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DELETE FROM retrieval_config WHERE agent_id = '*' AND namespace = '*'");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("default retrieval config", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenRequiredTableIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DROP TABLE jobs");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("Missing required table 'public.jobs'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvGrantIsRevoked()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "REVOKE INSERT ON memories FROM memsrv");

            var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
            Assert.NotEqual(0, exitCode);
            Assert.Contains("missing INSERT on 'public.memories'", stderr, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MemCtlVerifySchemaFailsWhenMemsrvIsNologin()
    {
        await WithDisposableDbAsync(async admin =>
        {
            // memsrv is cluster-wide. Hold a session advisory lock so the same
            // mechanical assertion in another test host cannot race restoration.
            await using var roleLock = new NpgsqlConnection(MaintenanceConnection);
            await roleLock.OpenAsync();
            await roleLock.ExecuteAsync("SELECT pg_advisory_lock(757002524895691804)");
            await roleLock.ExecuteAsync("ALTER ROLE memsrv NOLOGIN");
            try
            {
                var (exitCode, _, stderr) = await RunVerifySchemaAsync(admin);
                Assert.NotEqual(0, exitCode);
                Assert.Contains("NOLOGIN", stderr, StringComparison.Ordinal);
            }
            finally
            {
                await roleLock.ExecuteAsync("ALTER ROLE memsrv LOGIN");
                await roleLock.ExecuteAsync("SELECT pg_advisory_unlock(757002524895691804)");
            }
        });
    }

    [Fact]
    public async Task DisposableCloneRevalidatesTemplateAfterDifferentMigrationSet()
    {
        var migrationA = Path.Combine(Path.GetTempPath(), $"memsrv-migrations-a-{Guid.NewGuid():N}");
        var migrationB = Path.Combine(Path.GetTempPath(), $"memsrv-migrations-b-{Guid.NewGuid():N}");
        var databaseA = $"memory_test_{Guid.NewGuid():N}_branch_a";
        var databaseB = $"memory_test_{Guid.NewGuid():N}_branch_b";
        var databaseAAfterB = $"memory_test_{Guid.NewGuid():N}_branch_a_again";
        Directory.CreateDirectory(migrationA);
        Directory.CreateDirectory(migrationB);

        var sourceMigration = Path.Combine(_root, "migrations", "0001_init.sql");
        await File.WriteAllTextAsync(
            Path.Combine(migrationA, "0001_init.sql"),
            await File.ReadAllTextAsync(sourceMigration) + "\nCREATE TABLE branch_marker_a (id integer);\n");
        await File.WriteAllTextAsync(
            Path.Combine(migrationB, "0001_init.sql"),
            await File.ReadAllTextAsync(sourceMigration) + "\nCREATE TABLE branch_marker_b (id integer);\n");

        try
        {
            await TestDatabase.EnsureCurrentTemplateAndCloneAsync(databaseA, migrationA);
            Assert.True(await HasTableAsync(databaseA, "branch_marker_a"));
            Assert.False(await HasTableAsync(databaseA, "branch_marker_b"));

            await TestDatabase.EnsureCurrentTemplateAndCloneAsync(databaseB, migrationB);
            Assert.True(await HasTableAsync(databaseB, "branch_marker_b"));
            Assert.False(await HasTableAsync(databaseB, "branch_marker_a"));

            await TestDatabase.EnsureCurrentTemplateAndCloneAsync(databaseAAfterB, migrationA);
            Assert.True(await HasTableAsync(databaseAAfterB, "branch_marker_a"));
            Assert.False(await HasTableAsync(databaseAAfterB, "branch_marker_b"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            foreach (var database in new[] { databaseA, databaseB, databaseAAfterB })
            {
                await ExecuteAsync(
                    MaintenanceConnection,
                    $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            }
            await TestDatabase.EnsureCurrentTemplateAsync(Path.Combine(_root, "migrations"));
            Directory.Delete(migrationA, recursive: true);
            Directory.Delete(migrationB, recursive: true);
        }
    }

    [Fact]
    public async Task RoutingPolicyUpgradePreservesAcceptedObservationsAndReadsMissingRouteEvidence()
    {
        var migrations = Path.Combine(
            Path.GetTempPath(), $"memsrv-routing-upgrade-{Guid.NewGuid():N}");
        var database = $"memory_test_{Guid.NewGuid():N}_routing_upgrade";
        var admin = TestDatabase.BuildAdminConnection(database);
        Directory.CreateDirectory(migrations);
        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(_root, "migrations"), "*.sql")
                     .Where(path => string.Compare(
                         Path.GetFileName(path),
                         "0007_capture_routing_policy.sql",
                         StringComparison.Ordinal) < 0))
        {
            File.Copy(path, Path.Combine(migrations, Path.GetFileName(path)));
        }

        try
        {
            await ExecuteAsync(MaintenanceConnection, $"CREATE DATABASE \"{database}\"");
            DatabaseMigrator.Migrate(admin, migrations, logToConsole: false);
            Guid observationUuid;
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                observationUuid = await connection.ExecuteScalarAsync<Guid>(
                    """
                    WITH binding AS (
                      INSERT INTO capture_source_bindings
                        (stable_name, harness, agent_id, credential_hash,
                         allowed_namespaces)
                      VALUES
                        ('legacy-routing-upgrade', 'codex', 'capture:legacy',
                         'legacy-credential-hash', '{}')
                      RETURNING binding_uuid
                    ),
                    stream AS (
                      INSERT INTO capture_source_streams
                        (binding_uuid, source_session_id, effective_namespace,
                         route_basis, checkpoint_position)
                      SELECT binding_uuid, 'legacy-session', 'capture/unscoped',
                             'fallback', 0
                      FROM binding
                      RETURNING stream_uuid
                    )
                    INSERT INTO capture_observations
                      (stream_uuid, source_position, locator_kind,
                       locator_native_id, content_signature,
                       effective_namespace, route_basis, source, adapter,
                       safe_source_payload, scan_status)
                    SELECT stream_uuid, 0, 'native_id', 'legacy-record',
                           'legacy-signature', 'capture/unscoped', 'fallback',
                           '{"harness":"codex"}'::jsonb,
                           '{"name":"legacy","version":"1"}'::jsonb,
                           '{}'::jsonb, 'clean'
                    FROM stream
                    RETURNING observation_uuid
                    """);
                await connection.ExecuteAsync(
                    """
                    INSERT INTO captured_events
                      (observation_uuid, session_id, agent_id, namespace,
                       part_key, part_order, kind, actor, payload)
                    VALUES
                      (@observationUuid, 'legacy-session', 'capture:legacy',
                       'capture/unscoped', 'legacy-part', 0, 'message', 'user',
                       '{}'::jsonb)
                    """,
                    new { observationUuid });
            }

            string xminBefore;
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                xminBefore = await connection.ExecuteScalarAsync<string>(
                    """
                    SELECT xmin::text
                    FROM capture_observations
                    WHERE observation_uuid = @observationUuid
                    """,
                    new { observationUuid })
                    ?? throw new InvalidOperationException("Seeded observation was not found.");
            }

            File.Copy(
                Path.Combine(_root, "migrations", "0007_capture_routing_policy.sql"),
                Path.Combine(migrations, "0007_capture_routing_policy.sql"));
            DatabaseMigrator.Migrate(admin, migrations, logToConsole: false);

            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                Assert.Equal(
                    xminBefore,
                    await connection.ExecuteScalarAsync<string>(
                        """
                        SELECT xmin::text
                        FROM capture_observations
                        WHERE observation_uuid = @observationUuid
                        """,
                        new { observationUuid }));
                Assert.Equal(
                    "O",
                    await connection.ExecuteScalarAsync<string>(
                        """
                        SELECT tgenabled::text
                        FROM pg_trigger
                        WHERE tgrelid = 'capture_observations'::regclass
                          AND tgname = 'capture_observations_immutable'
                        """));
            }

            var envelope = Assert.Single(
                await new OperatorCaptureReads(admin)
                    .ReadCapturedEventEnvelopesAsync(observationUuid));
            Assert.Null(envelope.Observation.RouteEvidence);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(
                MaintenanceConnection,
                $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            Directory.Delete(migrations, recursive: true);
        }
    }

    [Fact]
    public async Task SourceIdentityUpgradeReusesThePopulatedStreamAndItsEstablishedTraceSession()
    {
        var migrations = Path.Combine(
            Path.GetTempPath(), $"memsrv-source-identity-upgrade-{Guid.NewGuid():N}");
        var database = $"memory_test_{Guid.NewGuid():N}_source_identity_upgrade";
        var admin = TestDatabase.BuildAdminConnection(database);
        Guid bindingUuid = Guid.NewGuid();
        Guid streamUuid = Guid.NewGuid();
        Guid observationUuid = Guid.NewGuid();
        byte[] signatureKey = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        const string legacyPathIdentity = "codex-rollout-legacy-path";
        const string externalSessionId = "01970000-0000-7000-8000-000000000149";
        const string childId = "01970000-0000-7000-8000-000000000150";
        string establishedSessionId = $"capture:{bindingUuid}:{legacyPathIdentity}";
        string legacySourcePayloadJson = JsonSerializer.Serialize(new
        {
            type = "session_meta",
            payload = new
            {
                session_id = externalSessionId,
                id = childId,
                source = new { future_classifier = true },
                thread_source = "subagent",
                cli_version = "0.144.synthetic"
            }
        });
        using var legacySourcePayload = JsonDocument.Parse(legacySourcePayloadJson);
        using var legacyEventPayload = JsonDocument.Parse("""{"message":"legacy"}""");
        var legacySource = new CaptureSource(
            "codex", null, "session_meta", MaterialKind: "persisted_record");
        var legacyEvent = new CaptureEvent(
            "metadata/0", 0, "lifecycle", "harness",
            legacyEventPayload.RootElement.Clone(), null, null);
        string legacySignatureContent = JsonSerializer.Serialize(
            new
            {
                contractVersion = 1,
                sourceSessionId = legacyPathIdentity,
                locator = (CaptureSourceLocator)new CaptureSourceLocator.NativeId(
                    "legacy-session-meta"),
                sourceTimestamp = (CaptureSourceTimestamp?)null,
                source = legacySource,
                adapter = new CaptureAdapter("codex-synthetic-jsonl", "2"),
                sourcePayload = legacySourcePayload.RootElement.Clone(),
                events = new[] { legacyEvent },
                routeEvidence = (CaptureRouteEvidence?)null
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var hmac = new HMACSHA256(signatureKey);
        string legacySignature = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(legacySignatureContent)))
            .ToLowerInvariant();
        Directory.CreateDirectory(migrations);
        foreach (string path in Directory.EnumerateFiles(
                     Path.Combine(_root, "migrations"), "*.sql")
                     .Where(path => string.Compare(
                         Path.GetFileName(path),
                         "0008_capture_source_identity.sql",
                         StringComparison.Ordinal) < 0))
        {
            File.Copy(path, Path.Combine(migrations, Path.GetFileName(path)));
        }

        try
        {
            await ExecuteAsync(MaintenanceConnection, $"CREATE DATABASE \"{database}\"");
            DatabaseMigrator.Migrate(admin, migrations, logToConsole: false);
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                await connection.ExecuteAsync(
                    """
                    INSERT INTO capture_source_bindings
                      (binding_uuid, stable_name, harness, agent_id, credential_hash,
                       allowed_namespaces, content_signature_key)
                    VALUES
                      (@bindingUuid, 'legacy-source-identity-upgrade', 'codex',
                       'capture:legacy-upgrade', 'legacy-upgrade-credential', '{}',
                       @signatureKey);

                    INSERT INTO capture_source_streams
                      (stream_uuid, binding_uuid, source_session_id, effective_namespace,
                       route_basis, checkpoint_position)
                    VALUES
                      (@streamUuid, @bindingUuid, @legacyPathIdentity, 'capture/unscoped',
                       'fallback', 0);

                    INSERT INTO capture_observations
                      (observation_uuid, stream_uuid, source_position, locator_kind,
                       locator_native_id, content_signature, effective_namespace, route_basis,
                       source, adapter, safe_source_payload, scan_status)
                    VALUES
                      (@observationUuid, @streamUuid, 0, 'native_id', 'legacy-session-meta',
                       @legacySignature, 'capture/unscoped', 'fallback',
                       '{"harness":"codex","harnessVersion":null,"recordType":"session_meta","materialKind":"persisted_record"}'::jsonb,
                       '{"name":"codex-synthetic-jsonl","version":"2"}'::jsonb,
                       CAST(@sourcePayload AS jsonb), 'clean');

                    INSERT INTO captured_events
                      (observation_uuid, session_id, agent_id, namespace,
                       part_key, part_order, kind, actor, payload)
                    VALUES
                      (@observationUuid, @establishedSessionId, 'capture:legacy-upgrade',
                       'capture/unscoped', 'metadata/0', 0, 'lifecycle', 'harness',
                       '{"message":"legacy"}'::jsonb);
                    """,
                    new
                    {
                        bindingUuid,
                        streamUuid,
                        observationUuid,
                        signatureKey,
                        legacyPathIdentity,
                        establishedSessionId,
                        legacySignature,
                        sourcePayload = legacySourcePayloadJson
                    });
            }

            string observationXmin;
            await using (var connection = new NpgsqlConnection(admin))
            {
                await connection.OpenAsync();
                observationXmin = await connection.ExecuteScalarAsync<string>(
                    """
                    SELECT xmin::text
                    FROM capture_observations
                    WHERE observation_uuid = @observationUuid
                    """,
                    new { observationUuid })
                    ?? throw new InvalidOperationException("Legacy observation was not found.");
            }

            File.Copy(
                Path.Combine(_root, "migrations", "0008_capture_source_identity.sql"),
                Path.Combine(migrations, "0008_capture_source_identity.sql"));
            DatabaseMigrator.Migrate(admin, migrations, logToConsole: false);

            using var sourcePayload = JsonDocument.Parse(
                """{"type":"turn_context","payload":{"message":"continued"}}""");
            using var eventPayload = JsonDocument.Parse("""{"message":"continued"}""");
            var command = CaptureObservationCommand.FromRequest(new CaptureObservationRequest(
                1,
                externalSessionId,
                1,
                new CaptureLocator("native_id", "continued-record", null, null, null),
                null,
                new CaptureSource(
                    "codex", "0.144.synthetic", "turn_context",
                    MaterialKind: "persisted_record"),
                new CaptureAdapter("codex-synthetic-jsonl", "3"),
                sourcePayload.RootElement.Clone(),
                [
                    new CaptureEvent(
                        "message/0", 0, "message", "user",
                        eventPayload.RootElement.Clone(), null, null)
                ],
                SourceIdentity: new CaptureSourceIdentity(externalSessionId, childId)));
            var ingestion = new CaptureIngestion(
                admin,
                new NeverStoreGate(Path.Combine(_root, "config", "never_store.yaml")));
            var binding = new CaptureBindingContext(
                bindingUuid,
                "codex",
                "capture:legacy-upgrade",
                signatureKey,
                CaptureRoutingPolicy.Empty);
            var historicalRetry = await ingestion.ImportAsync(
                binding,
                CaptureObservationCommand.FromRequest(new CaptureObservationRequest(
                    1,
                    externalSessionId,
                    0,
                    new CaptureLocator(
                        "native_id", "legacy-session-meta", null, null, null),
                    null,
                    legacySource with { HarnessVersion = "0.144.synthetic" },
                    new CaptureAdapter("codex-synthetic-jsonl", "3"),
                    legacySourcePayload.RootElement.Clone(),
                    [legacyEvent],
                    SourceIdentity: new CaptureSourceIdentity(externalSessionId, childId))));
            Assert.Equal("already_accepted", historicalRetry.Status);
            Assert.Equal(observationUuid, historicalRetry.ObservationUuid);
            Assert.Equal(streamUuid, historicalRetry.Observation.SourceStreamUuid);

            var receipt = await ingestion.ImportAsync(
                binding,
                command);

            Assert.Equal(streamUuid, receipt.Observation.SourceStreamUuid);
            Assert.Equal(
                new CaptureSourceIdentity(externalSessionId, childId),
                receipt.Observation.SourceIdentity);
            Assert.Equal(
                establishedSessionId,
                Assert.Single(receipt.Events).Event.SessionId);
            var legacyEnvelope = Assert.Single(
                await new OperatorCaptureReads(admin)
                    .ReadCapturedEventEnvelopesAsync(observationUuid));
            Assert.Equal(establishedSessionId, legacyEnvelope.Event.SessionId);
            Assert.Equal(
                new CaptureSourceIdentity(externalSessionId, childId),
                legacyEnvelope.Observation.SourceIdentity);
            await using var verification = new NpgsqlConnection(admin);
            await verification.OpenAsync();
            Assert.Equal(
                observationXmin,
                await verification.ExecuteScalarAsync<string>(
                    """
                    SELECT xmin::text
                    FROM capture_observations
                    WHERE observation_uuid = @observationUuid
                    """,
                    new { observationUuid }));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(
                MaintenanceConnection,
                $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            Directory.Delete(migrations, recursive: true);
        }
    }

    private async Task WithDisposableDbAsync(Func<string, Task> body)
    {
        var dbName = $"memory_test_{Guid.NewGuid():N}_verify";
        var adminConnection = TestDatabase.BuildAdminConnection(dbName);

        await TestDatabase.EnsureCurrentTemplateAndCloneAsync(
            dbName,
            Path.Combine(_root, "migrations"));
        try
        {
            await body(adminConnection);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(MaintenanceConnection, $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private static async Task<bool> HasTableAsync(string databaseName, string tableName)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.BuildAdminConnection(databaseName));
        await connection.OpenAsync();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT to_regclass('public.' || @tableName) IS NOT NULL",
            new { tableName });
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunVerifySchemaAsync(string adminConnection) =>
        TestProcessRunner.RunMemCtlToExitAsync(
            new Dictionary<string, string> { ["MEMSRV_ADMIN_CONNECTION_STRING"] = adminConnection },
            "verify-schema");
}

// Shared no-fixture collection: serializes SchemaVerifierTests with
// MemoryServiceTests so cluster-wide memsrv role toggles never race a memsrv login.
[CollectionDefinition("database")]
public sealed class DatabaseCollection : ICollectionFixture<TestDatabaseFixture>;
