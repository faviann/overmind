using System.Diagnostics;

namespace MemSrv.Tests;

public sealed class RepositoryStructureTests
{
    [Fact]
    public void BindingAuthorityRecordsTheCleanCutPreservationLayer()
    {
        string root = TestProcessRunner.RepoRoot;
        string boundary = File.ReadAllText(
            Path.Combine(root, "docs/evidence-and-knowledge-boundary.md"));
        string decisions = File.ReadAllText(Path.Combine(root, "docs/decisions.md"));

        Assert.Contains(
            "The superseded Phase 2 specification may be removed from the working tree",
            boundary,
            StringComparison.Ordinal);
        Assert.Contains("Git history", boundary, StringComparison.Ordinal);
        Assert.Contains("durable issues and pull requests", boundary, StringComparison.Ordinal);

        Assert.Contains(
            "Git history and durable issues and pull requests preserve the retired architecture",
            decisions,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredCaptureDocumentsAreAbsent()
    {
        string root = TestProcessRunner.RepoRoot;
        string[] retiredDocuments =
        [
            "docs/capture-safety-budgets.md",
            "docs/conversation-capture-phase2-spec.md",
            "docs/research/local-codex-claude-capture-surfaces.md"
        ];

        foreach (string path in retiredDocuments)
        {
            Assert.False(
                File.Exists(Path.Combine(root, path)),
                $"Retired capture document still exists: {path}");
        }
    }

    [Fact]
    public void MoraineOwnershipCancelsTheImportTimeSessionPreservationBlocker()
    {
        string root = TestProcessRunner.RepoRoot;
        string decisions = File.ReadAllText(Path.Combine(root, "docs/decisions.md"));
        string phaseOneSpec = File.ReadAllText(
            Path.Combine(root, "docs/memory-server-phase1-spec.md"));

        Assert.Contains(
            "Moraine owns durable external conversation and session evidence",
            decisions,
            StringComparison.Ordinal);
        Assert.Contains(
            "The old Overmind import-time session-preservation requirement",
            decisions,
            StringComparison.Ordinal);
        Assert.Contains("It no longer blocks the v1.0.0 tag", decisions, StringComparison.Ordinal);
        Assert.Contains(
            "Import-time external-session preservation is not an Overmind requirement",
            phaseOneSpec,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ActiveDocumentationRoutesOnlyToRetainedResponsibilities()
    {
        string root = TestProcessRunner.RepoRoot;
        string[] activeDocuments =
        [
            "AGENTS.md",
            "CONTEXT.md",
            "README.md",
            "docs/agents/domain.md",
            "docs/testing.md",
            "docs/deployment-contract.md",
            "docs/design-rules.md",
            "docs/evidence-and-knowledge-boundary.md",
            "docs/memory-server-phase1-spec.md",
            "docs/decisions.md",
            "docs/write-safety.md"
        ];
        string[] retiredRoutes =
        [
            "capture-safety-budgets.md",
            "conversation-capture-phase2-spec.md",
            "local-codex-claude-capture-surfaces.md"
        ];

        foreach (string path in activeDocuments)
        {
            string content = File.ReadAllText(Path.Combine(root, path));
            foreach (string retiredRoute in retiredRoutes)
            {
                Assert.DoesNotContain(retiredRoute, content, StringComparison.Ordinal);
            }
        }

        string glossary = File.ReadAllText(Path.Combine(root, "CONTEXT.md"));
        Assert.Contains("**Moraine**", glossary, StringComparison.Ordinal);
        Assert.Contains("**Local Capture Proof**", glossary, StringComparison.Ordinal);
        Assert.Contains("**Central Evidence Aggregation**", glossary, StringComparison.Ordinal);
        Assert.Contains("**Knowledge Provenance Integration**", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("**Capture ", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("**Captured ", glossary, StringComparison.Ordinal);
        Assert.DoesNotContain("inaccessible legacy", glossary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RetainedGlossaryAndDecisionChainDescribeTheCurrentBoundary()
    {
        string root = TestProcessRunner.RepoRoot;
        string glossary = File.ReadAllText(Path.Combine(root, "CONTEXT.md"));
        string decisions = File.ReadAllText(Path.Combine(root, "docs/decisions.md"));

        Assert.Contains("Every row belongs to\nexactly one namespace", glossary, StringComparison.Ordinal);
        Assert.Contains("It identifies the provisioned actor", glossary, StringComparison.Ordinal);
        Assert.Contains("only ever retrieved by its owning agent", glossary, StringComparison.Ordinal);
        Assert.Contains("Lifecycle: open → checked_out → open | done | abandoned", glossary, StringComparison.Ordinal);
        Assert.Contains("the full trace stays retrievable by reference, never inlined", glossary, StringComparison.Ordinal);
        Assert.Contains("Everything else\n(FTS index", glossary, StringComparison.Ordinal);

        Assert.Contains("No retired capture caller remains", decisions, StringComparison.Ordinal);
        Assert.Contains(
            "2026-08-31 #210/#225 decision then reversed the active-tree retention rule",
            decisions,
            StringComparison.Ordinal);
        Assert.Contains(
            "2026-08-31 #210/#225 decision cancels that",
            decisions,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Temporary capture callers map", decisions, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredCaptureCategoriesAreAbsentFromRepositoryAndPackageInputs()
    {
        string root = TestProcessRunner.RepoRoot;
        IReadOnlyList<string> repositoryFiles = GetTrackedRepositoryFiles(root);
        HashSet<string> intentionalProbeFiles = new(StringComparer.Ordinal)
        {
            "tests/MemSrv.Tests/RepositoryStructureTests.cs",
            "tests/MemSrv.Tests/PublicSurfaceRemovalTests.cs",
            "tests/MemSrv.Tests/HttpTransportTests.cs"
        };
        string[] retiredCategoryMarkers =
        [
            "CaptureAdapters",
            "CodexCaptureTracer",
            "/capture/",
            "memctl capture",
            "case \"capture\":",
            "MEMSRV_CAPTURE_",
            ".env.capture",
            "mcap_",
            "CaptureCredential",
            "CaptureSourceBinding",
            "capture/unscoped",
            "capture_sources",
            "capture_observations",
            "captured_events",
            "capture_pairing",
            "capture-runtime",
            "capture-console",
            "CaptureWake",
            "capture-hook",
            "capture_hook",
            "codex-capture-hooks",
            "smoke-capture-runtime"
        ];

        Assert.NotEmpty(repositoryFiles);
        foreach (string repositoryEntry in repositoryFiles)
        {
            if (intentionalProbeFiles.Contains(repositoryEntry))
            {
                continue;
            }

            string content = File.ReadAllText(Path.Combine(root, repositoryEntry));
            foreach (string marker in retiredCategoryMarkers)
            {
                Assert.False(
                    repositoryEntry.Contains(marker, StringComparison.OrdinalIgnoreCase)
                        || content.Contains(marker, StringComparison.OrdinalIgnoreCase),
                    $"Retired capture category marker '{marker}' found in {repositoryEntry}");
            }
        }
    }

    [Fact]
    public void RetiredCaptureSubstrateIsAbsentFromRepositoryGraph()
    {
        string root = TestProcessRunner.RepoRoot;
        string[] removedPaths =
        [
            "migrations/0002_capture_slice.sql",
            "migrations/0003_capture_stream_contract.sql",
            "migrations/0004_capture_locator_timestamp.sql",
            "migrations/0005_capture_relationship_stream_scope.sql",
            "migrations/0006_capture_runtime_update_grants.sql",
            "migrations/0007_capture_routing_policy.sql",
            "migrations/0008_capture_source_identity.sql",
            "migrations/0009_capture_outcome.sql",
            "migrations/0010_capture_pairing.sql",
            "src/MemSrv.Core/CaptureAuthority.cs",
            "src/MemSrv.Core/CaptureEnrollment.cs",
            "src/MemSrv.Core/CaptureFidelityPolicy.cs",
            "src/MemSrv.Core/CaptureIngestion.cs",
            "src/MemSrv.Core/CaptureLedger.cs",
            "src/MemSrv.Core/CaptureModels.cs",
            "src/MemSrv.Core/CaptureOutcomes.cs",
            "src/MemSrv.Core/CapturePairing.cs",
            "src/MemSrv.Core/CaptureRouting.cs",
            "src/MemSrv.Core/GovernedSerializationStream.cs",
            "src/MemSrv.Core/OperatorCaptureReads.cs",
            "tests/MemSrv.Tests/CaptureLedgerCoreTests.cs",
            "tests/MemSrv.Tests/CaptureObservationSizeTests.cs",
            "tests/MemSrv.Tests/CaptureOutcomeTests.cs",
            "tests/MemSrv.Tests/CaptureSafetyTests.cs",
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

        foreach (string path in removedPaths)
        {
            string absolutePath = Path.Combine(root, path);
            Assert.False(
                File.Exists(absolutePath) || Directory.Exists(absolutePath),
                $"Deleted capture substrate path still exists: {path}");
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

    private static IReadOnlyList<string> GetTrackedRepositoryFiles(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("ls-files");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("-z");

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to inspect the tracked repository graph.");
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git ls-files failed: {error}");

        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(IsContractRegion)
            .Where(path => !IsGeneratedOutput(path))
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsContractRegion(string path) =>
        !path.Contains('/', StringComparison.Ordinal)
        || path.StartsWith("config/", StringComparison.Ordinal)
        || path.StartsWith("packages/", StringComparison.Ordinal)
        || path.StartsWith("tools/", StringComparison.Ordinal)
        || path.StartsWith(".github/workflows/", StringComparison.Ordinal)
        || path.StartsWith("fixtures/", StringComparison.Ordinal)
        || path.StartsWith("tests/", StringComparison.Ordinal)
        || path.StartsWith("docs/", StringComparison.Ordinal)
        || path.StartsWith("src/", StringComparison.Ordinal)
        || path.StartsWith("migrations/", StringComparison.Ordinal);

    private static bool IsGeneratedOutput(string path) =>
        path.Contains("/bin/", StringComparison.Ordinal)
        || path.Contains("/obj/", StringComparison.Ordinal)
        || path.Contains("/TestResults/", StringComparison.Ordinal);
}
