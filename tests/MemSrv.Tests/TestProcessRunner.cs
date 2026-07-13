using System.Diagnostics;

namespace MemSrv.Tests;

// The one consolidated child-process launcher for tests (issues #26/#30).
//
// Children are launched by executing the built apphosts directly
// (src/<Project>/bin/<Configuration>/<TFM>/<Project>) instead of
// `dotnet run --no-build`. `dotnet run` still performs MSBuild project
// evaluation on every launch, and concurrent launches from parallel test
// classes contend on obj/ state and MSBuild node reuse — under load this
// intermittently corrupts unrelated children ("Sequence contains no elements",
// empty memctl stdout). Direct apphost execution involves no MSBuild, so
// process-launching test classes are safe to run in parallel; it is also much
// faster (~0.2s per launch vs ~1.6s).
//
// Configuration and TFM are derived from this test assembly's own output path,
// so a Release test run launches Release apphosts. Like `--no-build`, the
// runner never triggers a build: a missing apphost fails with instructions.
//
// Environment is fully caller-supplied (connection strings, transport,
// key-file paths, stub $EDITOR, ...); the runner only fixes the mechanics:
// repo-root working directory, redirected streams, concurrent stream drains,
// bounded waits with kill-tree on timeout.
internal static class TestProcessRunner
{
    private static readonly Lazy<string> _repoRoot = new(FindRepoRoot);
    private static readonly Lazy<string> _memCtlPath = new(() => ResolveApphost("MemCtl"));
    private static readonly Lazy<string> _serverPath = new(() => ResolveApphost("MemSrv.Server"));

    public static string RepoRoot => _repoRoot.Value;

    // The memctl apphost (operator CLI).
    public static string MemCtlPath => _memCtlPath.Value;

    // The MemSrv.Server apphost. Public so call sites where another component
    // owns the process lifecycle (e.g. StdioClientTransport) can launch the
    // same binary the runner would.
    public static string ServerPath => _serverPath.Value;

    // Runs memctl to completion. Failure-tolerant: returns the exit code and
    // both streams so tests can assert on refusals too.
    public static Task<(int ExitCode, string Stdout, string Stderr)> RunMemCtlToExitAsync(
        IReadOnlyDictionary<string, string> environment, params string[] args) =>
        RunToExitAsync(
            CreateStartInfo(MemCtlPath, args, environment),
            TimeSpan.FromSeconds(60),
            $"memctl {string.Join(' ', args)}");

    public static Task<(int ExitCode, string Stdout, string Stderr)> RunMemCtlToExitAsync(
        string runtimeConnection,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        params string[] args)
    {
        var environment = new Dictionary<string, string>
        {
            ["MEMSRV_CONNECTION_STRING"] = runtimeConnection
        };
        foreach (var (key, value) in extraEnvironment ?? new Dictionary<string, string>())
        {
            environment[key] = value;
        }

        return RunMemCtlToExitAsync(environment, args);
    }

    public static async Task<string> RunMemCtlAsync(
        string runtimeConnection,
        IReadOnlyDictionary<string, string>? extraEnvironment,
        params string[] args)
    {
        var (exitCode, stdout, stderr) =
            await RunMemCtlToExitAsync(runtimeConnection, extraEnvironment, args);
        Assert.True(
            exitCode == 0,
            $"memctl {string.Join(' ', args)} failed with exit {exitCode}. stdout={stdout} stderr={stderr}");
        return stdout;
    }

    // Runs MemSrv.Server expecting it to exit on its own (e.g. fail-closed
    // startup tests). The timeout bounds how long a wedged server can hang the
    // suite before it is killed (entire tree) and the test fails; description
    // lets the caller name its scenario in that timeout diagnostic.
    public static Task<(int ExitCode, string Stdout, string Stderr)> RunServerToExitAsync(
        IReadOnlyDictionary<string, string> environment, TimeSpan timeout,
        string description = "MemSrv.Server") =>
        RunToExitAsync(CreateStartInfo(ServerPath, [], environment), timeout, description);

    // Starts a long-running MemSrv.Server (stdio or HTTP transport, chosen by
    // the caller's environment) with stdin/stdout/stderr redirected. The caller
    // owns the process and is responsible for stopping and disposing it.
    public static Process StartServer(IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = CreateStartInfo(ServerPath, [], environment);
        startInfo.RedirectStandardInput = true;
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start MemSrv.Server.");
    }

    private static ProcessStartInfo CreateStartInfo(
        string apphostPath, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(apphostPath)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }
        foreach (var (key, value) in environment)
        {
            startInfo.Environment[key] = value;
        }
        return startInfo;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunToExitAsync(
        ProcessStartInfo startInfo, TimeSpan timeout, string description)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {description}.");
        // Drain both streams concurrently so a full pipe buffer can't deadlock
        // the child, and bound the wait so a hung child fails the test instead
        // of hanging the suite.
        var stdoutPump = process.StandardOutput.ReadToEndAsync();
        var stderrPump = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutPump, stderrPump);
            throw new Xunit.Sdk.XunitException(
                $"{description} did not exit within {timeout.TotalSeconds:0}s.");
        }

        return (process.ExitCode, await stdoutPump, await stderrPump);
    }

    private static string ResolveApphost(string projectName)
    {
        var (configuration, tfm) = TestOutputSegments();
        var fileName = OperatingSystem.IsWindows() ? projectName + ".exe" : projectName;
        var path = Path.Combine(RepoRoot, "src", projectName, "bin", configuration, tfm, fileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Apphost not found at '{path}'. Tests never build child projects " +
                "(mirroring --no-build); run `dotnet build memsrv.sln` first.");
        }
        return path;
    }

    // Derives <Configuration>/<TFM> from this assembly's own
    // bin/<Configuration>/<TFM> output path, so a Release test run resolves
    // Release apphosts instead of silently launching stale Debug ones.
    private static (string Configuration, string Tfm) TestOutputSegments()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(TestProcessRunner).Assembly.Location);
        var tfmDirectory = assemblyDirectory is null ? null : new DirectoryInfo(assemblyDirectory);
        var configurationDirectory = tfmDirectory?.Parent;
        var binDirectory = configurationDirectory?.Parent;
        if (tfmDirectory is null || configurationDirectory is null || binDirectory is null
            || !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Test assembly directory '{assemblyDirectory}' is not of the form " +
                "bin/<Configuration>/<TFM>; cannot resolve which apphosts to launch.");
        }
        return (configurationDirectory.Name, tfmDirectory.Name);
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
