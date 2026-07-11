using System.Diagnostics;

namespace MemSrv.Tests;

// The bearer-key file is a credential source. A malformed entry must fail the
// server closed. This is asserted through the public seam docs/testing.md wants
// — real process startup — rather than by calling AgentKeyStore.Load directly:
// an HTTP server pointed at a malformed key file must exit non-zero, naming the
// problem, before it ever serves a request. Key loading happens before any DB
// connection, so these tests need no database.
public sealed class ServerStartupTests
{
    private readonly string _root = FindRepoRoot();

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

    private async Task<(int ExitCode, string Stdout, string Stderr)> StartHttpServerAsync(string keysPath)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(_root, "src/MemSrv.Server/MemSrv.Server.csproj"));
        startInfo.ArgumentList.Add("--no-build");
        startInfo.Environment["MEMSRV_TRANSPORT"] = "http";
        startInfo.Environment["MEMSRV_HTTP_URL"] = "http://127.0.0.1:0";
        startInfo.Environment["MEMSRV_AGENT_KEYS_PATH"] = keysPath;
        // A connection string is read into options but never used: key loading
        // throws before the host (and any DB connection) is built.
        startInfo.Environment["MEMSRV_CONNECTION_STRING"] = "Host=127.0.0.1;Port=1;Database=unused;Username=none;Password=none";

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start MemSrv.Server.");
        // Drain both streams concurrently so neither can block the process on a
        // full pipe buffer.
        var stdoutPump = process.StandardOutput.ReadToEndAsync();
        var stderrPump = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutPump, stderrPump);
            throw new Xunit.Sdk.XunitException("Server did not exit within 30s on a malformed key file (expected fail-closed).");
        }

        return (process.ExitCode, await stdoutPump, await stderrPump);
    }

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
