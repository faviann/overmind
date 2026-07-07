using Microsoft.Extensions.Configuration;

namespace MemSrv.Core;

public static class Configuration
{
    public static MemSrvOptions Load(string basePath)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = config.GetSection("MemSrv").Get<MemSrvOptions>() ?? new MemSrvOptions();
        options.ConnectionString = NormalizeConnectionString(FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMSRV_CONNECTION_STRING"),
            Environment.GetEnvironmentVariable("MEMSRV_DEV_RUNTIME_CONNECTION"),
            options.ConnectionString));
        options.AdminConnectionString = NormalizeConnectionString(FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMSRV_ADMIN_CONNECTION_STRING"),
            Environment.GetEnvironmentVariable("MEMSRV_DEV_ADMIN_CONNECTION"),
            options.AdminConnectionString));
        options.AgentId = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_AGENT_ID"), options.AgentId);
        options.Namespace = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_NAMESPACE"), options.Namespace);
        options.SessionId = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_SESSION_ID"), options.SessionId);
        return options;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    // Npgsql only parses keyword connection strings; infra tooling speaks
    // postgres:// URLs. Accept both.
    private static string NormalizeConnectionString(string value)
    {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var uri = new Uri(value);
        var parts = new List<string>
        {
            $"Host={uri.Host}",
            $"Port={(uri.Port > 0 ? uri.Port : 5432)}"
        };

        var database = uri.AbsolutePath.TrimStart('/');
        if (database.Length > 0)
        {
            parts.Add($"Database={Uri.UnescapeDataString(database)}");
        }

        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo[0].Length > 0)
        {
            parts.Add($"Username={Uri.UnescapeDataString(userInfo[0])}");
        }

        if (userInfo.Length > 1)
        {
            parts.Add($"Password={Uri.UnescapeDataString(userInfo[1])}");
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var keyValue = pair.Split('=', 2);
            parts.Add($"{Uri.UnescapeDataString(keyValue[0])}={Uri.UnescapeDataString(keyValue.Length > 1 ? keyValue[1] : "")}");
        }

        return string.Join(";", parts);
    }
}
