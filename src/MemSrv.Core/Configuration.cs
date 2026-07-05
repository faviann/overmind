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
        options.ConnectionString = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMSRV_CONNECTION_STRING"),
            Environment.GetEnvironmentVariable("MEMSRV_DEV_RUNTIME_CONNECTION"),
            options.ConnectionString);
        options.AdminConnectionString = FirstNonEmpty(
            Environment.GetEnvironmentVariable("MEMSRV_ADMIN_CONNECTION_STRING"),
            Environment.GetEnvironmentVariable("MEMSRV_DEV_ADMIN_CONNECTION"),
            options.AdminConnectionString);
        options.AgentId = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_AGENT_ID"), options.AgentId);
        options.Namespace = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_NAMESPACE"), options.Namespace);
        options.SessionId = FirstNonEmpty(Environment.GetEnvironmentVariable("MEMSRV_SESSION_ID"), options.SessionId);
        return options;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
}
