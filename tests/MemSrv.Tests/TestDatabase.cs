using Dapper;
using MemSrv.Core;
using Npgsql;
using System.Security.Cryptography;
using System.Text;

namespace MemSrv.Tests;

public static class TestDatabase
{
    private static readonly Dictionary<Type, Task> ClassDatabaseResets = [];
    private static readonly object ClassDatabaseResetLock = new();
    public const string EnvironmentVariable = "MEMSRV_TEST_DATABASE";
    public const string TemplateName = "memory_test_template";
    private const string Host = "127.0.0.1";
    private const int Port = 55432;
    private const string AdminUser = "overmind";
    private const string AdminPassword = "overmind_dev";
    private const string RuntimePassword = "memsrv_dev";
    private static string? _databaseName;
    private static string? _runtimeRole;

    public static string DatabaseName => _databaseName
        ?? throw new InvalidOperationException("The test database fixture has not started.");

    public static string MaintenanceConnection => BuildConnection("postgres", AdminUser, AdminPassword);
    public static string AdminConnection => BuildConnection(DatabaseName, AdminUser, AdminPassword);
    public static string RuntimeConnection => BuildConnection(
        DatabaseName,
        _runtimeRole ?? throw new InvalidOperationException("The test runtime role has not been provisioned."),
        RuntimePassword);
    public static string AdminUrl =>
        $"postgres://{AdminUser}:{AdminPassword}@{Host}:{Port}/{Uri.EscapeDataString(DatabaseName)}";

    public static string ResolveDatabaseName(string? explicitName) =>
        string.IsNullOrWhiteSpace(explicitName) ? $"memory_test_{Guid.NewGuid():N}" : explicitName;

    public static string CreatedAtComment() => $"overmind-test-created-at={DateTimeOffset.UtcNow:O}";

    internal static void SetDatabaseName(string databaseName) => _databaseName = databaseName;
    internal static void SetRuntimeRole(string runtimeRole) => _runtimeRole = runtimeRole;

    public static string BuildAdminConnection(string databaseName) =>
        BuildConnection(databaseName, AdminUser, AdminPassword);

    public static async Task EnsureCurrentTemplateAndCloneAsync(string databaseName, string migrationsPath)
    {
        await using var templateLock = await AcquireTemplateLockAsync();
        await EnsureCurrentTemplateUnderLockAsync(migrationsPath);
        NpgsqlConnection.ClearAllPools();
        await ExecuteMaintenanceAsync($"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)} WITH (FORCE)");
        await ExecuteMaintenanceAsync(
            $"CREATE DATABASE {QuoteIdentifier(databaseName)} TEMPLATE {QuoteIdentifier(TemplateName)}");
        await ExecuteMaintenanceAsync(
            $"COMMENT ON DATABASE {QuoteIdentifier(databaseName)} IS {QuoteLiteral(CreatedAtComment())}");
    }

    public static async Task EnsureCurrentTemplateAsync(string migrationsPath)
    {
        await using var templateLock = await AcquireTemplateLockAsync();
        await EnsureCurrentTemplateUnderLockAsync(migrationsPath);
        NpgsqlConnection.ClearAllPools();
    }

    public static Task ResetSessionDatabaseOnceAsync(Type testClass, string migrationsPath)
    {
        lock (ClassDatabaseResetLock)
        {
            if (!ClassDatabaseResets.TryGetValue(testClass, out var reset))
            {
                reset = EnsureCurrentTemplateAndCloneAsync(DatabaseName, migrationsPath);
                ClassDatabaseResets.Add(testClass, reset);
            }
            return reset;
        }
    }

    private static async Task EnsureCurrentTemplateUnderLockAsync(string migrationsPath)
    {
        var fingerprint = MigrationFingerprint(migrationsPath);
        await using var connection = new NpgsqlConnection(MaintenanceConnection);
        await connection.OpenAsync();
        var currentFingerprint = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = @name",
            new { name = TemplateName });

        if (currentFingerprint == fingerprint)
        {
            return;
        }

        await connection.ExecuteAsync(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @name AND pid <> pg_backend_pid()",
            new { name = TemplateName });
        await connection.ExecuteAsync($"DROP DATABASE IF EXISTS {QuoteIdentifier(TemplateName)}");
        await connection.ExecuteAsync($"CREATE DATABASE {QuoteIdentifier(TemplateName)}");

        DatabaseMigrator.Migrate(BuildAdminConnection(TemplateName), migrationsPath, logToConsole: false);
        await connection.ExecuteAsync(
            $"COMMENT ON DATABASE {QuoteIdentifier(TemplateName)} IS {QuoteLiteral(fingerprint)}");
    }

    private static string MigrationFingerprint(string migrationsPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(migrationsPath, "*.sql").Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            hash.AppendData(File.ReadAllBytes(path));
        }
        return $"overmind-test-migrations-sha256={Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static async Task<FileStream> AcquireTemplateLockAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "overmind-test-template.lock");
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100);
            }
        }
    }

    private static async Task ExecuteMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MaintenanceConnection);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
    private static string QuoteLiteral(string value) => $"'{value.Replace("'", "''")}'";

    private static string BuildConnection(string databaseName, string username, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = databaseName,
            Username = username,
            Password = password
        }.ConnectionString;
}

public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private readonly string _root = FindRepoRoot();
    private readonly string _databaseName = TestDatabase.ResolveDatabaseName(
        Environment.GetEnvironmentVariable(TestDatabase.EnvironmentVariable));
    private readonly string _runtimeRole = $"memory_test_role_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        TestDatabase.SetDatabaseName(_databaseName);
        TestDatabase.SetRuntimeRole(_runtimeRole);
        await ExecuteMaintenanceAsync(
            $"CREATE ROLE {QuoteIdentifier(_runtimeRole)} LOGIN PASSWORD 'memsrv_dev' IN ROLE memsrv");
        await TestDatabase.EnsureCurrentTemplateAndCloneAsync(
            _databaseName,
            Path.Combine(_root, "migrations"));
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteMaintenanceAsync($"DROP DATABASE IF EXISTS {QuoteIdentifier(_databaseName)} WITH (FORCE)");
        await ExecuteMaintenanceAsync($"DROP ROLE IF EXISTS {QuoteIdentifier(_runtimeRole)}");
    }

    private static async Task ExecuteMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(TestDatabase.MaintenanceConnection);
        await connection.OpenAsync();
        await connection.ExecuteAsync(sql);
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
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
