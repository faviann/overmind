namespace MemSrv.Tests;

public sealed class RepositoryStructureTests
{
    [Fact]
    public void ForbiddenPropositionContractRejectsRetiredPresentTenseResponsibility()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertForbiddenPropositionsAbsent(
                "synthetic active document",
                "Overmind operates capture ingestion.",
                ["Overmind operates capture ingestion"]));
    }

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
        AssertForbiddenPropositionsAbsent(
            "docs/evidence-and-knowledge-boundary.md",
            boundary,
            [
                "The superseded Phase 2 specification must remain in the working tree",
                "The superseded Phase 2 specification stays in the working tree",
                "The historical Phase 2 specification must remain in the active tree",
                "requires that historical specification to remain",
                "capture documents stay while the shipped code"
            ]);

        Assert.Contains(
            "Git history and durable issues and pull requests preserve the retired architecture",
            decisions,
            StringComparison.Ordinal);
        AssertForbiddenPropositionsAbsent(
            "docs/decisions.md",
            decisions,
            [
                "The superseded Phase 2 specification must remain in the active tree",
                "The active-tree retention exception remains in force"
            ]);
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
        AssertForbiddenPropositionsAbsent(
            "docs/decisions.md",
            decisions,
            [
                "The old Overmind import-time session-preservation requirement remains binding",
                "The old Overmind import-time session-preservation requirement blocks v1.0.0",
                "The import-time session-preservation requirement stands, uncancelled",
                "requirement itself stands, uncancelled"
            ]);
        AssertForbiddenPropositionsAbsent(
            "docs/memory-server-phase1-spec.md",
            phaseOneSpec,
            [
                "Import-time external-session preservation is an Overmind requirement",
                "Import-time session preservation must land before the v1.0.0 tag",
                "Must land before the v1.0.0 tag",
                "The requirement itself stands, uncancelled"
            ]);
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
        string[] retiredResponsibilityPropositions =
        [
            "Overmind owns external conversation evidence",
            "Overmind stores external conversation evidence",
            "Overmind captures external conversations",
            "Overmind ingests external conversations",
            "Overmind owns capture ingestion",
            "Overmind operates capture ingestion",
            "Overmind provides capture ingestion",
            "Capture ingestion is an Overmind responsibility",
            "Overmind owns capture routing",
            "Overmind operates capture routing",
            "Overmind provides capture routing",
            "Capture routing is an Overmind responsibility",
            "Overmind routes captured conversations",
            "Overmind owns capture pairing",
            "Overmind operates capture pairing",
            "Overmind provides capture pairing",
            "Capture pairing is an Overmind responsibility",
            "Overmind pairs capture sources",
            "Overmind enrolls capture sources",
            "Overmind issues capture credentials",
            "Overmind manages capture credentials",
            "Overmind provisions capture credentials",
            "Overmind ships a capture runtime",
            "Overmind operates a capture runtime",
            "Overmind runs capture hooks",
            "Capture runtime is an Overmind responsibility"
        ];

        foreach (string path in activeDocuments)
        {
            string content = File.ReadAllText(Path.Combine(root, path));
            foreach (string retiredRoute in retiredRoutes)
            {
                Assert.DoesNotContain(retiredRoute, content, StringComparison.Ordinal);
            }
            AssertForbiddenPropositionsAbsent(path, content, retiredResponsibilityPropositions);
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
        string glossary = NormalizeWhitespace(
            File.ReadAllText(Path.Combine(root, "CONTEXT.md")));
        string decisions = NormalizeWhitespace(
            File.ReadAllText(Path.Combine(root, "docs/decisions.md")));

        Assert.Contains(
            "Every memory and trace belongs to exactly one namespace",
            glossary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Every row belongs", glossary, StringComparison.Ordinal);
        Assert.Contains("Server-derived and never self-asserted", glossary, StringComparison.Ordinal);
        Assert.Contains(
            "Codex and Claude Code remain distinct provisioned actors even when one person operates both",
            glossary,
            StringComparison.Ordinal);
        Assert.Contains("default namespace and allowed namespaces", glossary, StringComparison.Ordinal);
        Assert.Contains("Provisioning owns its lifecycle", glossary, StringComparison.Ordinal);
        Assert.Contains("not the application", glossary, StringComparison.Ordinal);
        Assert.Contains("explicit namespace still requires authorization", glossary, StringComparison.Ordinal);
        Assert.Contains("trusted transport or process context", glossary, StringComparison.Ordinal);
        Assert.Contains("every event in the run shares that session", glossary, StringComparison.Ordinal);
        Assert.Contains("authorization still applies to every member", glossary, StringComparison.Ordinal);
        Assert.Contains("review:<proposal_uuid>", glossary, StringComparison.Ordinal);
        Assert.Contains("human:<name>", glossary, StringComparison.Ordinal);
        Assert.Contains("reviewer, never the proposing agent", glossary, StringComparison.Ordinal);
        Assert.Contains("status='proposed'", glossary, StringComparison.Ordinal);
        Assert.Contains("not yet trusted and hidden by default", glossary, StringComparison.Ordinal);
        Assert.Contains(
            "Operator approval or edit-then-approval is the only route to shared knowledge; rejection leaves it rejected",
            glossary,
            StringComparison.Ordinal);
        Assert.Contains("agents cannot approve it", glossary, StringComparison.Ordinal);
        Assert.Contains("decision provenance remain available for audit", glossary, StringComparison.Ordinal);
        Assert.Contains("visible only to its owning agent", glossary, StringComparison.Ordinal);
        Assert.Contains("open → checked_out → open | done | abandoned", glossary, StringComparison.Ordinal);
        Assert.Contains("exactly one checkout owner", glossary, StringComparison.Ordinal);
        Assert.Contains("Only its checkout owner may check in", glossary, StringComparison.Ordinal);
        Assert.Contains("an open check-in is a handoff", glossary, StringComparison.Ordinal);
        Assert.Contains("full trace remains retrievable by reference and is never inlined", glossary, StringComparison.Ordinal);
        Assert.Contains(
            "Indexes, exports, and other representations of ledger content are derived, rebuildable projections and never the sole place ledger truth exists",
            glossary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Everything outside that ledger", glossary, StringComparison.Ordinal);

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
    public async Task RetiredCaptureCategoriesAreAbsentFromRepositoryAndPackageInputs()
    {
        string root = TestProcessRunner.RepoRoot;
        IReadOnlyList<string> repositoryFiles = await GetTrackedRepositoryFilesAsync(root);
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
            string content = File.ReadAllText(Path.Combine(root, repositoryEntry));
            foreach (string marker in retiredCategoryMarkers)
            {
                bool containsMarker =
                    repositoryEntry.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    || content.Contains(marker, StringComparison.OrdinalIgnoreCase);
                Assert.True(
                    !containsMarker || IsIntentionalProbe(repositoryEntry, marker),
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

    private static async Task<IReadOnlyList<string>> GetTrackedRepositoryFilesAsync(string root)
    {
        var result = await TestProcessRunner.RunCommandToExitAsync(
            "git",
            ["ls-files", "--cached", "-z"],
            "",
            TimeSpan.Zero,
            new Dictionary<string, string>(),
            TimeSpan.FromSeconds(30),
            "tracked repository graph inspection");
        Assert.True(result.ExitCode == 0, $"git ls-files failed: {result.Stderr}");

        return result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(path => !IsGeneratedOutput(path))
            .Where(path => File.Exists(Path.Combine(root, path)))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static bool IsIntentionalProbe(string path, string marker)
    {
        if (string.Equals(
            path,
            "tests/MemSrv.Tests/RepositoryStructureTests.cs",
            StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(
            path,
            "tests/MemSrv.Tests/PublicSurfaceRemovalTests.cs",
            StringComparison.Ordinal))
        {
            return marker is "/capture/" or "memctl capture" or "MEMSRV_CAPTURE_" or "mcap_";
        }

        return string.Equals(
                path,
                "tests/MemSrv.Tests/HttpTransportTests.cs",
                StringComparison.Ordinal)
            && string.Equals(marker, "mcap_", StringComparison.Ordinal);
    }

    private static bool IsGeneratedOutput(string path) =>
        path.Contains("/bin/", StringComparison.Ordinal)
        || path.Contains("/obj/", StringComparison.Ordinal)
        || path.Contains("/TestResults/", StringComparison.Ordinal);

    private static string NormalizeWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static void AssertForbiddenPropositionsAbsent(
        string source,
        string content,
        IReadOnlyList<string> forbiddenPropositions)
    {
        string normalizedContent = NormalizeWhitespace(content);
        foreach (string proposition in forbiddenPropositions)
        {
            Assert.False(
                normalizedContent.Contains(proposition, StringComparison.OrdinalIgnoreCase),
                $"Forbidden retired proposition found in {source}: {proposition}");
        }
    }
}
