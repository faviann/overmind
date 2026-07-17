namespace MemSrv.Tests;

public sealed class TestDatabaseLifecycleTests
{
    [Fact]
    public void MissingExternalAdminConnectionUsesComposeDevelopmentDatabase()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            TestDatabase.ResolveMaintenanceConnection(null));

        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal(55432, connection.Port);
        Assert.Equal("postgres", connection.Database);
        Assert.Equal("overmind", connection.Username);
        Assert.Equal("overmind_dev", connection.Password);
    }

    [Fact]
    public void ExternalAdminUrlSuppliesClusterLocationAndAuthority()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            TestDatabase.ResolveMaintenanceConnection(
                "postgres://test_admin:p%40ss@database.internal:6432/control?SSL Mode=Require"));

        Assert.Equal("database.internal", connection.Host);
        Assert.Equal(6432, connection.Port);
        Assert.Equal("control", connection.Database);
        Assert.Equal("test_admin", connection.Username);
        Assert.Equal("p@ss", connection.Password);
        Assert.Equal(Npgsql.SslMode.Require, connection.SslMode);
    }

    [Fact]
    public void DatabaseConnectionsRetainExternalConnectionOptions()
    {
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(
            TestDatabase.BuildAdminConnection(
                "isolated_clone",
                "Host=database.internal;Port=6432;Database=control;Username=test_admin;" +
                "Password=secret;SSL Mode=Require"));

        Assert.Equal("isolated_clone", connection.Database);
        Assert.Equal("database.internal", connection.Host);
        Assert.Equal(6432, connection.Port);
        Assert.Equal("test_admin", connection.Username);
        Assert.Equal("secret", connection.Password);
        Assert.Equal(Npgsql.SslMode.Require, connection.SslMode);
    }

    [Fact]
    public void ExplicitDatabaseNameIsPreserved()
    {
        Assert.Equal("pinned_test_database", TestDatabase.ResolveDatabaseName("pinned_test_database"));
    }

    [Fact]
    public void MissingDatabaseNameGeneratesIsolatedSessionName()
    {
        var databaseName = TestDatabase.ResolveDatabaseName(null);

        Assert.StartsWith("memory_test_", databaseName, StringComparison.Ordinal);
        Assert.Matches("^memory_test_[a-f0-9]{32}$", databaseName);
    }

    [Fact]
    public void LifecycleCommentCarriesParseableCreationTime()
    {
        const string prefix = "overmind-test-created-at=";
        var comment = TestDatabase.CreatedAtComment();

        Assert.StartsWith(prefix, comment, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.TryParse(comment[prefix.Length..], out _));
    }
}
