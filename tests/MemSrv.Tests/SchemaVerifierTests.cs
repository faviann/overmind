using Dapper;
using MemSrv.Core;
using Npgsql;

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
