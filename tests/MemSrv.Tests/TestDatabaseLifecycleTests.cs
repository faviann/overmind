namespace MemSrv.Tests;

public sealed class TestDatabaseLifecycleTests
{
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
}
