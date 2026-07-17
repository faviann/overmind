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
    public const string AdminConnectionEnvironmentVariable = "MEMSRV_TEST_ADMIN_CONNECTION_STRING";
    public const string TemplateName = "memory_test_template";
    private const string ComposeMaintenanceConnection =
        "Host=127.0.0.1;Port=55432;Database=postgres;Username=overmind;Password=overmind_dev";
    private const string RuntimePassword = "memsrv_dev";
    private static string? _databaseName;
    private static string? _runtimeRole;

    public static string DatabaseName => _databaseName
        ?? throw new InvalidOperationException("The test database fixture has not started.");

    public static string MaintenanceConnection => ResolveMaintenanceConnection(
        Environment.GetEnvironmentVariable(AdminConnectionEnvironmentVariable));
    public static string AdminConnection => BuildAdminConnection(DatabaseName);
    public static string RuntimeConnection => BuildRuntimeConnection(
        DatabaseName,
        _runtimeRole ?? throw new InvalidOperationException("The test runtime role has not been provisioned."));
    public static string AdminUrl => BuildPostgresUrl(AdminConnection);

    public static string ResolveDatabaseName(string? explicitName) =>
        string.IsNullOrWhiteSpace(explicitName) ? $"memory_test_{Guid.NewGuid():N}" : explicitName;

    public static string CreatedAtComment() => $"overmind-test-created-at={DateTimeOffset.UtcNow:O}";

    internal static void BeginSession(string databaseName, string runtimeRole)
    {
        lock (ClassDatabaseResetLock)
        {
            _databaseName = databaseName;
            _runtimeRole = runtimeRole;
            ClassDatabaseResets.Clear();
        }
    }

    public static string ResolveMaintenanceConnection(string? externalConnection) =>
        NormalizeConnectionString(string.IsNullOrWhiteSpace(externalConnection)
            ? ComposeMaintenanceConnection
            : externalConnection);

    public static string BuildAdminConnection(string databaseName) =>
        BuildAdminConnection(databaseName, MaintenanceConnection);

    public static string BuildAdminConnection(string databaseName, string maintenanceConnection)
    {
        var builder = new NpgsqlConnectionStringBuilder(NormalizeConnectionString(maintenanceConnection))
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

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

    public static Task PrepareClassDatabaseAsync(Type testClass, string migrationsPath)
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

    private static string BuildRuntimeConnection(string databaseName, string username)
    {
        var builder = new NpgsqlConnectionStringBuilder(MaintenanceConnection)
        {
            Database = databaseName,
            Username = username,
            Password = RuntimePassword
        };
        return builder.ConnectionString;
    }

    private static string NormalizeConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432
        };
        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length > 0)
        {
            builder.Database = Uri.UnescapeDataString(database);
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo[0].Length > 0)
        {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }
        if (userInfo.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = pair.Split('=', 2);
            builder[Uri.UnescapeDataString(keyValue[0])] =
                Uri.UnescapeDataString(keyValue.Length > 1 ? keyValue[1] : "");
        }
        return builder.ConnectionString;
    }

    private static string BuildPostgresUrl(string connectionString)
    {
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        var rawHost = connection.Host ?? throw new InvalidOperationException("Test admin host is required.");
        var username = connection.Username ?? throw new InvalidOperationException("Test admin username is required.");
        var password = connection.Password ?? throw new InvalidOperationException("Test admin password is required.");
        var database = connection.Database ?? throw new InvalidOperationException("Test admin database is required.");
        var host = rawHost.Contains(':', StringComparison.Ordinal) ? $"[{rawHost}]" : rawHost;
        var query = connection
            .Where(pair => pair.Key is not ("Host" or "Port" or "Database" or "Username" or "Password"))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value?.ToString() ?? "")}")
            .ToArray();
        return $"postgres://{Uri.EscapeDataString(username)}:" +
            $"{Uri.EscapeDataString(password)}@{host}:{connection.Port}/" +
            $"{Uri.EscapeDataString(database)}" +
            (query.Length == 0 ? "" : $"?{string.Join('&', query)}");
    }
}

public sealed class TestDatabaseFixture : IAsyncLifetime
{
    private readonly string _databaseName = TestDatabase.ResolveDatabaseName(
        Environment.GetEnvironmentVariable(TestDatabase.EnvironmentVariable));
    private readonly string _runtimeRole = $"memory_test_role_{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        TestDatabase.BeginSession(_databaseName, _runtimeRole);
        await ExecuteMaintenanceAsync(
            $"CREATE ROLE {QuoteIdentifier(_runtimeRole)} LOGIN PASSWORD 'memsrv_dev' IN ROLE memsrv");
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
}
