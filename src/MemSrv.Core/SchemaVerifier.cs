using Dapper;
using Npgsql;

namespace MemSrv.Core;

/// <summary>
/// Verifies that a migrated database matches the schema, grant, and trigger
/// contract owned by overmind. Intended to run against a disposable database
/// (homelab-iac: create → <c>memctl migrate</c> → <c>memctl verify-schema</c> →
/// drop) so it may write probe rows freely, but every probe is rolled back so it
/// is also safe against a real migrated database.
/// </summary>
public static class SchemaVerifier
{
    private const string MemsrvRole = "memsrv";
    private const string ProbeNamespace = "memory-system";
    private const string AppendOnlyMessage = "traces are append-only";

    private static readonly string[] RequiredTables =
    [
        "namespaces", "traces", "trace_snapshots", "memories",
        "retrieval_config", "workstreams", "jobs",
        "capture_source_bindings", "capture_source_streams",
        "capture_observations", "captured_events", "captured_event_relationships"
    ];

    private static readonly string[] BootstrapNamespaces = ["memory-system", "homelab", "capture/unscoped"];

    // Table grants memsrv must hold, mirroring migrations/0001_init.sql. DELETE is
    // never listed here and is asserted absent everywhere by a separate check.
    private static readonly (string Table, string[] Privileges)[] ExpectedGrants =
    [
        ("traces", ["SELECT", "INSERT"]),
        ("trace_snapshots", ["SELECT", "INSERT"]),
        ("memories", ["SELECT", "INSERT", "UPDATE"]),
        ("workstreams", ["SELECT", "INSERT", "UPDATE"]),
        ("jobs", ["SELECT", "INSERT", "UPDATE"]),
        ("retrieval_config", ["SELECT", "INSERT", "UPDATE"]),
        ("namespaces", ["SELECT", "INSERT", "UPDATE"]),
        ("capture_source_bindings", ["SELECT", "INSERT"]),
        ("capture_source_streams", ["SELECT", "INSERT"]),
        ("capture_observations", ["SELECT", "INSERT"]),
        ("captured_events", ["SELECT", "INSERT"]),
        ("captured_event_relationships", ["SELECT", "INSERT"]),
    ];

    public static async Task<SchemaVerificationResult> VerifyAsync(string adminConnectionString)
    {
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            throw new InvalidOperationException("Admin connection string is required to verify the schema.");
        }

        var result = new SchemaVerificationResult();

        await using var conn = new NpgsqlConnection(adminConnectionString);
        await conn.OpenAsync();

        var existingTables = await CheckTablesAsync(conn, result);
        await CheckFunctionAndTriggerAsync(conn, result);
        await CheckBootstrapRowsAsync(conn, existingTables, result);
        await CheckAppendOnlyTriggerAsync(conn, existingTables, result);
        await CheckGrantsAsync(conn, existingTables, result);

        return result;
    }

    private static async Task<HashSet<string>> CheckTablesAsync(NpgsqlConnection conn, SchemaVerificationResult result)
    {
        var present = (await conn.QueryAsync<string>(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r'")).ToHashSet();

        foreach (var table in RequiredTables)
        {
            if (!present.Contains(table))
            {
                result.Fail($"Missing required table 'public.{table}'.");
            }
        }

        return present;
    }

    private static async Task CheckFunctionAndTriggerAsync(NpgsqlConnection conn, SchemaVerificationResult result)
    {
        var functionExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace " +
            "WHERE n.nspname = 'public' AND p.proname = 'forbid_mutation')");
        if (!functionExists)
        {
            result.Fail("Missing required function 'public.forbid_mutation()' (guards the traces append-only trigger).");
        }

        var triggerExists = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid " +
            "WHERE c.relname = 'traces' AND t.tgname = 'traces_immutable' AND NOT t.tgisinternal)");
        if (!triggerExists)
        {
            result.Fail("Missing append-only trigger 'traces_immutable' on 'public.traces'.");
        }

        foreach (var (table, trigger) in new[]
        {
            ("capture_observations", "capture_observations_immutable"),
            ("captured_events", "captured_events_immutable"),
            ("captured_event_relationships", "captured_event_relationships_immutable")
        })
        {
            var exists = await conn.ExecuteScalarAsync<bool>(
                """
                SELECT EXISTS (
                  SELECT 1 FROM pg_trigger t
                  JOIN pg_class c ON c.oid = t.tgrelid
                  WHERE c.relname = @table AND t.tgname = @trigger AND NOT t.tgisinternal
                )
                """,
                new { table, trigger });
            if (!exists)
            {
                result.Fail($"Missing append-only trigger '{trigger}' on 'public.{table}'.");
            }
        }
    }

    private static async Task CheckBootstrapRowsAsync(
        NpgsqlConnection conn, HashSet<string> existingTables, SchemaVerificationResult result)
    {
        if (existingTables.Contains("namespaces"))
        {
            foreach (var name in BootstrapNamespaces)
            {
                var exists = await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM namespaces WHERE name = @name)", new { name });
                if (!exists)
                {
                    result.Fail($"Missing bootstrap namespace '{name}'.");
                }
            }
        }

        if (existingTables.Contains("retrieval_config"))
        {
            var exists = await conn.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM retrieval_config WHERE agent_id = '*' AND namespace = '*')");
            if (!exists)
            {
                result.Fail("Missing default retrieval config row (agent_id='*', namespace='*').");
            }
        }
    }

    // Proves the append-only guard structurally: insert a probe trace, then attempt
    // UPDATE and DELETE as the (admin-capable) verifier identity and require both to
    // fail with the append-only error. Everything runs inside a transaction that is
    // always rolled back, so no probe row survives.
    private static async Task CheckAppendOnlyTriggerAsync(
        NpgsqlConnection conn, HashSet<string> existingTables, SchemaVerificationResult result)
    {
        if (!existingTables.Contains("traces") || !existingTables.Contains("namespaces"))
        {
            return; // Missing-table check already reported the real problem.
        }

        await using var tx = await conn.BeginTransactionAsync();
        Guid probeUuid;
        try
        {
            probeUuid = await conn.QuerySingleAsync<Guid>(
                "INSERT INTO traces (session_id, agent_id, namespace, event_type, content) " +
                "VALUES ('verify-schema', 'verify-schema', @ns, 'note', '{}'::jsonb) RETURNING trace_uuid",
                new { ns = ProbeNamespace }, tx);
        }
        catch (PostgresException ex)
        {
            result.Fail($"Could not insert a probe row into 'traces' for the append-only check: {ex.SqlState} {ex.MessageText}.");
            await tx.RollbackAsync();
            return;
        }

        await AssertMutationBlockedAsync(conn, tx, result, "UPDATE",
            "UPDATE traces SET event_type = 'verify-schema' WHERE trace_uuid = @u", probeUuid);
        await AssertMutationBlockedAsync(conn, tx, result, "DELETE",
            "DELETE FROM traces WHERE trace_uuid = @u", probeUuid);

        await tx.RollbackAsync();
    }

    private static async Task AssertMutationBlockedAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, SchemaVerificationResult result,
        string verb, string sql, Guid probeUuid)
    {
        const string savepoint = "append_only_probe";
        await tx.SaveAsync(savepoint);
        try
        {
            await conn.ExecuteAsync(sql, new { u = probeUuid }, tx);
            result.Fail($"traces is not append-only: {verb} as an admin-capable identity succeeded (expected error '{AppendOnlyMessage}').");
            await tx.RollbackAsync(savepoint);
        }
        catch (PostgresException ex)
        {
            await tx.RollbackAsync(savepoint);
            if (ex.SqlState != "P0001" ||
                !ex.MessageText.Contains(AppendOnlyMessage, StringComparison.OrdinalIgnoreCase))
            {
                result.Fail($"traces {verb} raised an unexpected error instead of the append-only guard: {ex.SqlState} {ex.MessageText}.");
            }
        }
    }

    private static async Task CheckGrantsAsync(
        NpgsqlConnection conn, HashSet<string> existingTables, SchemaVerificationResult result)
    {
        var canLogin = await conn.ExecuteScalarAsync<bool?>(
            "SELECT rolcanlogin FROM pg_roles WHERE rolname = @role", new { role = MemsrvRole });
        if (canLogin is null)
        {
            result.Fail($"Application role '{MemsrvRole}' does not exist; migrations require it to be provisioned first.");
            return;
        }

        if (canLogin == false)
        {
            result.Fail($"Application role '{MemsrvRole}' exists but is NOLOGIN; it must be a LOGIN role so the server can connect.");
        }

        if (!await conn.ExecuteScalarAsync<bool>(
                "SELECT has_schema_privilege(@role, 'public', 'USAGE')", new { role = MemsrvRole }))
        {
            result.Fail($"Role '{MemsrvRole}' is missing USAGE on schema 'public'.");
        }

        foreach (var (table, privileges) in ExpectedGrants)
        {
            if (!existingTables.Contains(table))
            {
                continue; // Missing-table check already reported this.
            }

            foreach (var privilege in privileges)
            {
                var granted = await conn.ExecuteScalarAsync<bool>(
                    "SELECT has_table_privilege(@role, @table, @privilege)",
                    new { role = MemsrvRole, table = $"public.{table}", privilege });
                if (!granted)
                {
                    result.Fail($"Role '{MemsrvRole}' is missing {privilege} on 'public.{table}'.");
                }
            }
        }

        await CheckCaptureUpdateGrantsAsync(conn, existingTables, result);

        // traces (and its snapshots) are append-only by grant as well as by trigger.
        foreach (var table in new[]
        {
            "traces", "trace_snapshots", "capture_observations",
            "captured_events", "captured_event_relationships"
        })
        {
            if (!existingTables.Contains(table))
            {
                continue;
            }

            if (await conn.ExecuteScalarAsync<bool>(
                    "SELECT has_table_privilege(@role, @table, 'UPDATE')",
                    new { role = MemsrvRole, table = $"public.{table}" }))
            {
                result.Fail($"Role '{MemsrvRole}' must not have UPDATE on 'public.{table}' (append-only contract).");
            }
        }

        // No DELETE grant on any table in public — by grant, the ledger is no-delete.
        var deletable = await conn.QueryAsync<string>(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace " +
            "WHERE n.nspname = 'public' AND c.relkind = 'r' " +
            "AND has_table_privilege(@role, c.oid, 'DELETE') ORDER BY c.relname",
            new { role = MemsrvRole });
        foreach (var table in deletable)
        {
            result.Fail($"Role '{MemsrvRole}' has a DELETE grant on 'public.{table}'; no DELETE grants are permitted.");
        }

        // Sequences: USAGE on every public sequence (identity columns need it to INSERT).
        var sequences = await conn.QueryAsync<string>(
            "SELECT sequencename FROM pg_sequences WHERE schemaname = 'public'");
        foreach (var sequence in sequences)
        {
            if (!await conn.ExecuteScalarAsync<bool>(
                    "SELECT has_sequence_privilege(@role, @sequence, 'USAGE')",
                    new { role = MemsrvRole, sequence = $"public.{sequence}" }))
            {
                result.Fail($"Role '{MemsrvRole}' is missing USAGE on sequence 'public.{sequence}'.");
            }
        }
    }

    private static async Task CheckCaptureUpdateGrantsAsync(
        NpgsqlConnection conn,
        HashSet<string> existingTables,
        SchemaVerificationResult result)
    {
        foreach (var (table, allowedColumns) in new[]
        {
            ("capture_source_bindings", Array.Empty<string>()),
            ("capture_source_streams", new[] { "checkpoint_position", "updated_at" })
        })
        {
            if (!existingTables.Contains(table))
            {
                continue;
            }

            var columns = await conn.QueryAsync<string>(
                """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = @table
                ORDER BY ordinal_position
                """,
                new { table });
            foreach (var column in columns)
            {
                bool granted = await conn.ExecuteScalarAsync<bool>(
                    "SELECT has_column_privilege(@role, @table, @column, 'UPDATE')",
                    new
                    {
                        role = MemsrvRole,
                        table = $"public.{table}",
                        column
                    });
                bool expected = allowedColumns.Contains(column, StringComparer.Ordinal);
                if (expected && !granted)
                {
                    result.Fail(
                        $"Role '{MemsrvRole}' is missing UPDATE on 'public.{table}.{column}'.");
                }
                else if (!expected && granted)
                {
                    result.Fail(
                        $"Role '{MemsrvRole}' must not have UPDATE on 'public.{table}.{column}'.");
                }
            }
        }
    }
}

public sealed class SchemaVerificationResult
{
    private readonly List<string> _failures = [];

    public IReadOnlyList<string> Failures => _failures;

    public bool Passed => _failures.Count == 0;

    internal void Fail(string message) => _failures.Add(message);
}
