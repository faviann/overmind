using Microsoft.Extensions.Configuration;
using Npgsql;

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
        options.AllowedNamespaces = ParseNamespaces(Environment.GetEnvironmentVariable("MEMSRV_ALLOWED_NAMESPACES"), options.AllowedNamespaces);
        options.AgentKeysPath = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_AGENT_KEYS_PATH"), options.AgentKeysPath);
        options.HttpUrl = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_HTTP_URL"), options.HttpUrl);
        options.Transport = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_TRANSPORT"), options.Transport);
        return options;
    }

    // Stdio-mode allowlist source. Comma-separated; unset leaves the list
    // empty, so the context allows only the default namespace.
    private static string[] ParseNamespaces(string? value, string[] fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    // Npgsql only parses keyword connection strings; infra tooling speaks
    // postgres:// URLs. Accept both.
    internal static string NormalizeConnectionString(string value)
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
}
