using MemSrv.Core;

var root = Directory.GetCurrentDirectory();
var options = Configuration.Load(root);

try
{
    if (args.Length == 0)
    {
        Usage();
        return 2;
    }

    var command = args[0];
    switch (command)
    {
        case "migrate":
            DatabaseMigrator.Migrate(options.AdminConnectionString, Path.Combine(root, "migrations"));
            Console.WriteLine("migrations applied");
            return 0;

        case "verify-schema":
            return await VerifySchemaAsync(options);

        case "pending":
            await PendingAsync(options, args.Skip(1).FirstOrDefault());
            return 0;

        case "show":
            RequireArgs(args, 2);
            await ShowAsync(options, Guid.Parse(args[1]));
            return 0;

        case "approve":
            RequireArgs(args, 2);
            await ApproveAsync(
                options,
                Guid.Parse(args[1]),
                RequireOption(args, "--by"),
                HasOption(args, "--edit"),
                FindOption(args, "--content-file"));
            return 0;

        case "reject":
            RequireArgs(args, 2);
            await RejectAsync(options, Guid.Parse(args[1]), RequireOption(args, "--by"), RequireOption(args, "--reason"));
            return 0;

        case "retire":
            RequireArgs(args, 2);
            await RetireAsync(
                options,
                Guid.Parse(args[1]),
                RequireOption(args, "--by"),
                RequireOption(args, "--reason"));
            return 0;

        case "release":
            RequireArgs(args, 2);
            await ReleaseAsync(options, Guid.Parse(args[1]));
            return 0;

        case "workstream":
            RequireArgs(args, 3);
            if (!string.Equals(args[1], "release", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unknown workstream command '{args[1]}'.");
            }
            await ReleaseAsync(options, Guid.Parse(args[2]));
            return 0;

        case "why":
            RequireArgs(args, 2);
            await WhyAsync(options, Guid.Parse(args[1]));
            return 0;

        case "consumed":
            RequireArgs(args, 2);
            await ConsumedAsync(options, args[1]);
            return 0;

        case "trace":
            RequireArgs(args, 2);
            await TraceAsync(options, args[1]);
            return 0;

        case "capture":
            RequireArgs(args, 2);
            return await CaptureAsync(options, args);

        default:
            Usage();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static async Task<int> VerifySchemaAsync(MemSrvOptions options)
{
    if (string.IsNullOrWhiteSpace(options.AdminConnectionString))
    {
        Console.Error.WriteLine("verify-schema requires MEMSRV_ADMIN_CONNECTION_STRING.");
        return 2;
    }

    var result = await SchemaVerifier.VerifyAsync(options.AdminConnectionString);
    if (result.Passed)
    {
        Console.WriteLine("schema verification passed");
        return 0;
    }

    Console.Error.WriteLine($"schema verification FAILED ({result.Failures.Count} problem(s)):");
    foreach (var failure in result.Failures)
    {
        Console.Error.WriteLine($"  - {failure}");
    }

    return 1;
}

static MemoryService Service(MemSrvOptions options) =>
    new(options.ConnectionString, new NeverStoreGate(options.NeverStorePath));

static async Task PendingAsync(MemSrvOptions options, string? @namespace)
{
    var rows = await Service(options).PendingAsync(@namespace);
    foreach (var row in rows)
    {
        Console.WriteLine($"{row.Uuid} {row.Namespace} {row.Type} source={row.SourceType}:{row.SourceId ?? "<none>"}");
        Console.WriteLine(row.Content);
        Console.WriteLine();
    }
}

static async Task ShowAsync(MemSrvOptions options, Guid uuid)
{
    var row = await Service(options).ShowAsync(uuid);
    Console.WriteLine($"{row.Uuid} {row.Namespace} {row.Type} {row.Visibility}/{row.Status} tier={row.Tier} v{row.Version}");
    Console.WriteLine($"source={row.SourceType}:{row.SourceId ?? "<none>"} agent={row.AgentId} session={row.SessionId ?? "<none>"}");
    Console.WriteLine($"created={row.CreatedAt:O} approved_by={row.ApprovedBy ?? "<none>"} approved_at={row.ApprovedAt?.ToString("O") ?? "<none>"}");
    Console.WriteLine($"retired={row.RetiredAt?.ToString("O") ?? "<none>"}");
    if (row.Supersedes.HasValue)
    {
        Console.WriteLine($"supersedes={row.Supersedes}");
    }

    if (row.SupersededBy.HasValue)
    {
        Console.WriteLine($"superseded_by={row.SupersededBy}");
    }

    Console.WriteLine();
    Console.WriteLine(row.Content);
}

static async Task ApproveAsync(MemSrvOptions options, Guid uuid, string by, bool edit, string? contentFile)
{
    if (edit && contentFile is not null)
    {
        throw new ArgumentException("--edit and --content-file cannot be used together.");
    }

    if (!edit && contentFile is null)
    {
        await Service(options).ApproveAsync(uuid, by);
        Console.WriteLine($"approved {uuid} by {by}");
        return;
    }

    var amendedContent = contentFile is not null
        ? await File.ReadAllTextAsync(contentFile)
        : await EditProposalAsync(options, uuid);
    var approvedUuid = await Service(options).ApproveAmendmentAsync(uuid, by, amendedContent);
    Console.WriteLine($"approved {approvedUuid} by {by} superseding {uuid}");
}

static async Task<string> EditProposalAsync(MemSrvOptions options, Guid uuid)
{
    var proposal = await Service(options).ShowAsync(uuid);
    if (proposal.Visibility != "shared" || proposal.Status != "proposed")
    {
        throw new InvalidOperationException($"Memory '{uuid}' is not a pending shared proposal.");
    }

    var path = Path.GetTempFileName();
    try
    {
        await File.WriteAllTextAsync(path, proposal.Content);
        var editor = Environment.GetEnvironmentVariable("EDITOR");
        if (string.IsNullOrWhiteSpace(editor))
        {
            editor = "nano";
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(editor)
        {
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(path);
        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start editor '{editor}'.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Editor '{editor}' exited with code {process.ExitCode}.");
        }

        return await File.ReadAllTextAsync(path);
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task RejectAsync(MemSrvOptions options, Guid uuid, string by, string reason)
{
    await Service(options).RejectAsync(uuid, by, reason);
    Console.WriteLine($"rejected {uuid} by {by}");
}

static async Task RetireAsync(MemSrvOptions options, Guid uuid, string by, string reason)
{
    await Service(options).RetireAsync(uuid, by, reason);
    Console.WriteLine($"retired {uuid}");
}

static async Task ReleaseAsync(MemSrvOptions options, Guid uuid)
{
    var row = await Service(options).ReleaseWorkstreamAsync(uuid);
    Console.WriteLine($"released {row.Uuid} ('{row.Title}') back to open");
}

static async Task WhyAsync(MemSrvOptions options, Guid uuid)
{
    var steps = await Service(options).WhyAsync(uuid);
    foreach (var step in steps)
    {
        Console.WriteLine($"memory {step.Uuid} v{step.Version} {step.Status} source={step.SourceType}:{step.SourceId ?? "<none>"}");
        if (step.SourceTrace is { } trace)
        {
            Console.WriteLine($"  trace {trace.TraceUuid} {trace.EventType} session={trace.SessionId} agent={trace.AgentId} ts={trace.Ts:O}");
            Console.WriteLine($"  {trace.Content}");
        }

        if (step.Supersedes.HasValue)
        {
            Console.WriteLine($"  supersedes {step.Supersedes}");
        }
    }
}

static async Task ConsumedAsync(MemSrvOptions options, string sessionId)
{
    var entries = await Service(options).ConsumedAsync(sessionId);
    foreach (var entry in entries)
    {
        Console.WriteLine(entry.Kind == "trace"
            ? $"{entry.Ts:O} trace {entry.Uuid} event={entry.Type}"
            : $"{entry.Ts:O} memory {entry.Uuid} type={entry.Type} source={entry.SourceType}:{entry.SourceId ?? "<none>"}");
    }
}

static async Task TraceAsync(MemSrvOptions options, string sessionId)
{
    var rows = await Service(options).TraceAsync(sessionId);
    foreach (var row in rows)
    {
        var refs = row.Refs is { Length: > 0 } ? string.Join(',', row.Refs) : "<none>";
        Console.WriteLine($"{row.Ts:O} {row.EventType} {row.TraceUuid} agent={row.AgentId} ns={row.Namespace} refs={refs}");
        Console.WriteLine(row.Content);
    }
}

static async Task<int> CaptureAsync(MemSrvOptions options, string[] args)
{
    var capture = new CaptureService(
        options.ConnectionString, new NeverStoreGate(options.NeverStorePath));
    switch (args[1])
    {
        case "enroll":
            RequireArgs(args, 3);
            string credentialPath = RequireOption(args, "--credential-file");
            string credential = (await File.ReadAllTextAsync(credentialPath)).Trim();
            var bindingUuid = await capture.EnrollAsync(
                args[2],
                RequireOption(args, "--harness"),
                RequireOption(args, "--agent-id"),
                credential,
                FindOption(args, "--namespace"));
            Console.WriteLine($"enrolled {bindingUuid} stable_name={args[2]}");
            Console.WriteLine(
                "LIMITATION: disabled non-production Codex synthetic capture only; " +
                "no scheduler, hooks, scanner product, or supported capture adapter.");
            return 0;

        case "receipt":
            RequireArgs(args, 3);
            var receipt = await capture.ReadReceiptAsync(Guid.Parse(args[2]));
            Console.WriteLine(
                $"{receipt.ObservationUuid} status={receipt.Status} " +
                $"namespace={receipt.EffectiveNamespace} route={receipt.RouteBasis}");
            Console.WriteLine(
                $"binding={receipt.StableName} harness={receipt.Harness} " +
                $"session={receipt.SourceSessionId} locator={receipt.SourceLocator}");
            Console.WriteLine($"content={receipt.SafeSourcePayload}");
            foreach (var item in receipt.Events)
            {
                Console.WriteLine($"event={item.TraceUuid} part={item.PartKey}");
            }
            Console.WriteLine(
                "LIMITATION: receipt is for the disabled non-production synthetic Codex slice.");
            return 0;

        default:
            throw new ArgumentException($"Unknown capture command '{args[1]}'.");
    }
}

static string? FindOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1].StartsWith("--", StringComparison.Ordinal) ? null : args[i + 1];
        }
    }

    return null;
}

static bool HasOption(string[] args, string name) => args.Contains(name, StringComparer.Ordinal);

static string RequireOption(string[] args, string name) =>
    FindOption(args, name) ?? throw new ArgumentException($"{name} is required.");

static void RequireArgs(string[] args, int count)
{
    if (args.Length < count)
    {
        throw new ArgumentException("Missing required argument.");
    }
}

static void Usage()
{
    Console.Error.WriteLine("memctl migrate");
    Console.Error.WriteLine("memctl verify-schema");
    Console.Error.WriteLine("memctl pending [namespace]");
    Console.Error.WriteLine("memctl show <uuid>");
    Console.Error.WriteLine("memctl approve <uuid> --by name [--edit | --content-file path]");
    Console.Error.WriteLine("memctl reject <uuid> --by name --reason reason");
    Console.Error.WriteLine("memctl retire <uuid> --by name --reason reason");
    Console.Error.WriteLine("memctl workstream release <uuid>");
    Console.Error.WriteLine("memctl release <uuid>  # compatibility alias");
    Console.Error.WriteLine("memctl why <uuid>");
    Console.Error.WriteLine("memctl consumed <session_id>");
    Console.Error.WriteLine("memctl trace <session_id>");
    Console.Error.WriteLine(
        "memctl capture enroll <stable_name> --harness codex --agent-id id " +
        "--credential-file path [--namespace name]");
    Console.Error.WriteLine("memctl capture receipt <observation_uuid>");
}
