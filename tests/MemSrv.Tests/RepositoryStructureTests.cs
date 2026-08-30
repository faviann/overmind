namespace MemSrv.Tests;

public sealed class RepositoryStructureTests
{
    [Fact]
    public void WorkstationCaptureProducerIsAbsentFromRepositoryGraph()
    {
        string root = TestProcessRunner.RepoRoot;
        string[] removedPaths =
        [
            "src/CaptureAdapters/AdapterContracts.cs",
            "src/CaptureAdapters/CaptureAdapters.csproj",
            "src/CaptureAdapters/CaptureRescanScheduler.cs",
            "src/CaptureAdapters/CaptureRuntimeState.cs",
            "src/CaptureAdapters/CodexJsonlAdapter.cs",
            "src/CaptureAdapters/CodexTranscriptDiscovery.cs",
            "src/CaptureAdapters/DisabledCaptureRuntime.cs",
            "src/CaptureAdapters/JsonAdapterHelpers.cs",
            "src/CaptureAdapters/JsonlSourceReader.cs",
            "src/CodexCaptureTracer/CaptureWakeForwarder.cs",
            "src/CodexCaptureTracer/CaptureWakeListener.cs",
            "src/CodexCaptureTracer/CodexCaptureTracer.csproj",
            "src/CodexCaptureTracer/Program.cs",
            ".env.capture.example",
            "Dockerfile.capture-runtime",
            "Dockerfile.capture-tracer",
            "compose.capture.yaml",
            "packages/codex-capture-hooks/0.147.0/README.md",
            "packages/codex-capture-hooks/0.147.0/hooks.json",
            "packages/codex-capture-hooks/0.147.0/install.sh",
            "packages/codex-capture-hooks/0.147.0/manifest.json",
            "packages/codex-capture-hooks/0.147.0/overmind-codex-wake-0.147.0",
            "packages/codex-capture-hooks/0.147.0/upgrade.sh",
            "tools/smoke-capture-runtime.sh",
            "fixtures/adapter-conformance/claude-code-2.1.201.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.120.parent-fork.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.absent-relationship.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.compaction-hooks.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.compaction.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.context.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.messages.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.nested-child.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.144.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.145.annotations.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.145.opaque.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.145.reasoning.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.145.tools.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.146.binary-media.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.77.compaction.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.77.parent-only.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-cli-0.90.fork-only.synthetic.jsonl",
            "fixtures/adapter-conformance/codex-terminal-invalid-utf8.synthetic.hex",
            "fixtures/adapter-conformance/codex-terminal-malformed-readable.synthetic.txt",
            "fixtures/transcripts/codex-synthetic.jsonl",
            "tests/MemSrv.Tests/CaptureAdapterConformanceTests.cs",
            "tests/MemSrv.Tests/CapturePackagingTests.cs",
            "tests/MemSrv.Tests/CaptureRuntimeStateTests.cs",
            "tests/MemSrv.Tests/CaptureScheduleTests.cs",
            "docs/capture-adapter-contract.md",
            "docs/capture-modules.md",
            "docs/capture-synthetic-slice.md",
            "docs/codex-capture-runtime.md"
        ];

        Assert.Equal(52, removedPaths.Length);

        foreach (string path in removedPaths)
        {
            string absolutePath = Path.Combine(root, path);
            Assert.False(
                File.Exists(absolutePath) || Directory.Exists(absolutePath),
                $"Deleted workstation capture producer path still exists: {path}");
        }

        Assert.DoesNotContain(
            "CaptureAdapters",
            File.ReadAllText(Path.Combine(root, "memsrv.sln")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CaptureAdapters",
            File.ReadAllText(Path.Combine(root, "tests/MemSrv.Tests/MemSrv.Tests.csproj")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CodexCaptureTracer",
            File.ReadAllText(Path.Combine(root, "tests/MemSrv.Tests/TestProcessRunner.cs")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "capture-runtime",
            File.ReadAllText(Path.Combine(root, ".github/workflows/ci.yml")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "smoke-capture-runtime",
            File.ReadAllText(Path.Combine(root, "Makefile")),
            StringComparison.Ordinal);
    }
}
