using DbUp;

namespace MemSrv.Core;

public static class DatabaseMigrator
{
    public static void Migrate(string adminConnectionString, string migrationsPath, bool logToConsole = true)
    {
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            throw new InvalidOperationException("Admin connection string is required to run migrations.");
        }

        var builder = DeployChanges.To
            .PostgresqlDatabase(adminConnectionString)
            .WithScriptsFromFileSystem(migrationsPath);

        var upgrader = (logToConsole ? builder.LogToConsole() : builder.LogToNowhere()).Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw result.Error;
        }
    }
}
