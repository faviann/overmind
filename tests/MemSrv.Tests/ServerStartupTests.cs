namespace MemSrv.Tests;

// The bearer-key file is a credential source. A malformed entry must fail the
// server closed. This is asserted through the public seam docs/testing.md wants
// — real process startup — rather than by calling AgentKeyStore.Load directly:
// an HTTP server pointed at a malformed key file must exit non-zero, naming the
// problem, before it ever serves a request. Key loading happens before any DB
// connection, so these tests need no database.
//
// Isolation (issue #30): this class deliberately stays OUTSIDE the "database"
// collection so it runs in parallel with the DB-backed classes. That is safe
// only because the server child is launched as a direct apphost via
// TestProcessRunner — not `dotnet run`, whose per-launch MSBuild evaluation
// races concurrent launches from the collection classes on obj/ state.
public sealed class ServerStartupTests
{
    [Theory]
    [InlineData("blank key", "key is blank",
        """
        keys:
          - key: ""
            agent_id: agent-a
            default_namespace: memory-system
            allowed_namespaces: [memory-system]
        """)]
    [InlineData("missing agent_id", "agent_id is blank",
        """
        keys:
          - key: real-key-123
            default_namespace: memory-system
            allowed_namespaces: [memory-system]
        """)]
    [InlineData("missing default_namespace", "default_namespace is blank",
        """
        keys:
          - key: real-key-123
            agent_id: agent-a
            allowed_namespaces: [memory-system]
        """)]
    [InlineData("empty allowlist", "allowed_namespaces is empty",
        """
        keys:
          - key: real-key-123
            agent_id: agent-a
            default_namespace: memory-system
            allowed_namespaces: []
        """)]
    [InlineData("default outside allowlist", "is not in allowed_namespaces",
        """
        keys:
          - key: real-key-123
            agent_id: agent-a
            default_namespace: memory-system
            allowed_namespaces: [homelab]
        """)]
    public async Task HttpServerFailsClosedOnMalformedKeyFile(string _, string expectedReason, string yaml)
    {
        var keysPath = Path.Combine(Path.GetTempPath(), $"bad-keys-{Guid.NewGuid():N}.yaml");
        await File.WriteAllTextAsync(keysPath, yaml);
        try
        {
            var (exitCode, stdout, stderr) = await StartHttpServerAsync(keysPath);
            Assert.True(exitCode != 0, $"Server started despite a malformed key file. stderr={stderr}");
            // The failure reason belongs on stderr; stdout must stay clean.
            Assert.Contains(expectedReason, stderr);
            Assert.True(stdout.Length == 0, $"Malformed-key startup wrote to stdout:{Environment.NewLine}{stdout}");
        }
        finally
        {
            File.Delete(keysPath);
        }
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> StartHttpServerAsync(string keysPath) =>
        TestProcessRunner.RunServerToExitAsync(new Dictionary<string, string>
        {
            ["MEMSRV_TRANSPORT"] = "http",
            ["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0",
            ["MEMSRV_AGENT_KEYS_PATH"] = keysPath,
            // A connection string is read into options but never used: key loading
            // throws before the host (and any DB connection) is built.
            ["MEMSRV_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=none;Password=none",
        }, timeout: TimeSpan.FromSeconds(30));
}
