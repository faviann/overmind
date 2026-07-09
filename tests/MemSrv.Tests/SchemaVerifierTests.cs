using Dapper;
using MemSrv.Core;
using Npgsql;
using System.Diagnostics;

namespace MemSrv.Tests;

// Each test owns a disposable database (create → migrate → verify/break → drop),
// mirroring the homelab-iac disposable verify step. Databases are uniquely named
// so the class runs in parallel with the rest of the suite without contention.
public sealed class SchemaVerifierTests
{
    private const string MaintenanceConnection =
        "Host=127.0.0.1;Port=55432;Database=postgres;Username=overmind;Password=overmind_dev";
    private readonly string _root = FindRepoRoot();

    [Fact]
    public async Task VerifyPassesOnFreshlyMigratedSchema()
    {
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
    public async Task MemCtlVerifySchemaFailsWhenMemsrvHasDeleteGrant()
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
    public async Task VerifyFailsWhenDefaultRetrievalConfigIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DELETE FROM retrieval_config WHERE agent_id = '*' AND namespace = '*'");

            var result = await SchemaVerifier.VerifyAsync(admin);
            Assert.False(result.Passed);
            Assert.Contains(result.Failures, f => f.Contains("default retrieval config", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task VerifyFailsWhenRequiredTableIsMissing()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "DROP TABLE jobs");

            var result = await SchemaVerifier.VerifyAsync(admin);
            Assert.False(result.Passed);
            Assert.Contains(result.Failures, f => f.Contains("Missing required table 'public.jobs'", StringComparison.Ordinal));
        });
    }

    [Fact]
    public async Task VerifyFailsWhenMemsrvGrantIsRevoked()
    {
        await WithDisposableDbAsync(async admin =>
        {
            await ExecuteAsync(admin, "REVOKE INSERT ON memories FROM memsrv");

            var result = await SchemaVerifier.VerifyAsync(admin);
            Assert.False(result.Passed);
            Assert.Contains(result.Failures, f => f.Contains("missing INSERT on 'public.memories'", StringComparison.Ordinal));
        });
    }

    private async Task WithDisposableDbAsync(Func<string, Task> body)
    {
        var dbName = $"verify_schema_test_{Guid.NewGuid():N}";
        var adminConnection =
            $"Host=127.0.0.1;Port=55432;Database={dbName};Username=overmind;Password=overmind_dev";

        await ExecuteAsync(MaintenanceConnection, $"CREATE DATABASE \"{dbName}\"");
        try
        {
            DatabaseMigrator.Migrate(adminConnection, Path.Combine(_root, "migrations"), logToConsole: false);
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

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunVerifySchemaAsync(string adminConnection)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemCtl/MemCtl.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("verify-schema");
        startInfo.Environment["MEMSRV_ADMIN_CONNECTION_STRING"] = adminConnection;

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start memctl.");
        var stderr = await process.StandardError.ReadToEndAsync();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "migrations")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repo root.");
    }
}
