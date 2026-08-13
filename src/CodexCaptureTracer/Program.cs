using CaptureAdapters;
using MemSrv.Core;
using System.Net.Http.Json;
using System.Text.Json;

const string LegacySyntheticEnableValue = "synthetic-non-production";
bool legacySyntheticDiagnostics = string.Equals(
    Environment.GetEnvironmentVariable("OVERMIND_CODEX_CAPTURE_ENABLE"),
    LegacySyntheticEnableValue,
    StringComparison.Ordinal);

string endpoint;
string credential;
string transcriptRoot;
string? archiveRoot;
string stateDirectory;
bool useLegacySyntheticDiscovery;
try
{
    endpoint = Required("OVERMIND_CAPTURE_URL").TrimEnd('/');
    if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
    {
        throw new InvalidOperationException("OVERMIND_CAPTURE_URL must be an absolute URL.");
    }
    useLegacySyntheticDiscovery = legacySyntheticDiagnostics
        && string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("OVERMIND_CODEX_SESSIONS_ROOT"));
    transcriptRoot = Path.GetFullPath(Required(
        useLegacySyntheticDiscovery
            ? "OVERMIND_CODEX_TRANSCRIPT_ROOT"
            : "OVERMIND_CODEX_SESSIONS_ROOT"));
    archiveRoot = useLegacySyntheticDiscovery
        ? null
        : Path.GetFullPath(Required("OVERMIND_CODEX_ARCHIVE_ROOT"));
    stateDirectory = Path.GetFullPath(
        Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_STATE_DIR")
        ?? transcriptRoot + ".overmind-state");
    credential = await ResolveCredentialAsync(endpoint, stateDirectory);
}
catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
{
    WriteDiagnostic("capture_runtime_configuration_invalid", FailureCode(ex));
    return 2;
}

// Fail closed before any source material is read: a tracer whose rule set is
// missing, empty, invalid, duplicated, unsupported, or un-loadable refuses to
// run and says why on stderr. Diagnostics never reach stdout.
NeverStoreGate safetyGate;
try
{
    var captureOptions = Configuration.Load(Directory.GetCurrentDirectory());
    safetyGate = new NeverStoreGate(
        captureOptions.NeverStorePath, captureOptions.NeverStoreLiteralsPath);
}
catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
{
    WriteDiagnostic("capture_runtime_configuration_invalid", FailureCode(ex));
    return 3;
}
if (!safetyGate.IsConfigured)
{
    CaptureOutcomeSummary outcome = CaptureOutcomeAggregation.Summarize(
    [
        CaptureOutcomeAggregation.SafetyFailure(
            "codex",
            CaptureOutcomeReason.ScannerPolicyUnavailable)
    ]);
    WriteDiagnostic(
        "capture_runtime_configuration_invalid",
        "safety_policy_unavailable");
    WriteOutcome(outcome);
    return 3;
}

var runtimeState = new FileCaptureRuntimeState(stateDirectory);
var adapter = new CodexJsonlAdapter();

async Task ScanAndDeliverAsync(
    CodexTranscriptStream transcript,
    CancellationToken cancellationToken,
    bool deliveryAuthorized)
{
    await CodexCaptureClaimer.ClaimCompletedAsync(
        adapter,
        transcript.Path,
        transcript.SourceStream,
        runtimeState,
        safetyGate,
        cancellationToken,
        transcript.TerminalAtEndOfFile,
        transcript.TranscriptIdentity,
        transcript.SourceIdentity);

    if (!deliveryAuthorized)
    {
        return;
    }

    CaptureRuntimeStreamState? stream = (await runtimeState.ReadAsync(cancellationToken))
        .Streams.SingleOrDefault(value =>
            string.Equals(
                value.SourceStream, transcript.SourceStream, StringComparison.Ordinal));
    if (stream is null || stream.Queue.Count == 0)
    {
        return;
    }

    foreach (CaptureRuntimeQueueItem queued in
        stream.Queue.OrderBy(item => item.SourcePosition))
    {
        _ = await runtimeState.DeliverAuthorizedAsync(
            transcript.SourceStream,
            queued,
            async token =>
            {
                CaptureServerReceiptState? receiptState = null;
                IReadOnlyList<string> responses =
                    await DisabledCaptureRuntime.RunClaimedFixtureAsync(
                        adapter,
                        transcript.Path,
                        transcript.SourceStream,
                        [queued],
                        new Uri(endpoint, UriKind.Absolute),
                        credential,
                        safetyGate,
                        (receipt, delivered, _) =>
                        {
                            receiptState = ValidateReceipt(receipt, delivered);
                            return Task.CompletedTask;
                        },
                        token,
                        transcript.TerminalAtEndOfFile,
                        transcript.TranscriptIdentity,
                        transcript.SourceIdentity);
                if (receiptState is null || responses.Count != 1)
                {
                    throw new InvalidDataException(
                        "Capture delivery did not return one conclusive receipt.");
                }
                return new CaptureRuntimeDeliveryResult<string>(
                    receiptState, responses[0]);
            },
            cancellationToken);
        WriteDiagnostic("capture_delivery_accepted");
    }
}

CaptureRescanSchedule schedule;
try
{
    schedule = CaptureRescanConfiguration.Load();
}
catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
{
    WriteDiagnostic(
        "capture_runtime_configuration_invalid",
        ex is InvalidOperationException ? "invalid_scan_schedule" : FailureCode(ex));
    return 2;
}
using var stopping = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

try
{
    async Task ScanCycleAsync(CancellationToken cancellationToken)
    {
        // Validate durable responsibility before any network activity. This is
        // content-free local state and does not scan transcript material.
        CaptureRuntimeSnapshot durableSnapshot;
        try
        {
            durableSnapshot = await runtimeState.ReadAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
        {
            WriteFailure(ex);
            return;
        }
        bool? persistedPaused;
        try
        {
            persistedPaused = await LoadInstructionPolicyAsync(
                stateDirectory, cancellationToken);
        }
        catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
        {
            WriteFailure(ex);
            return;
        }

        IReadOnlyList<CodexTranscriptStream> DiscoverStreams()
        {
            if (useLegacySyntheticDiscovery)
            {
                return CodexTranscriptDiscovery.Enumerate(transcriptRoot);
            }
            var responsibleSourceStreamsByTranscriptIdentity = durableSnapshot.Streams
                .Where(stream => stream.Queue.Count > 0)
                .ToDictionary(
                    stream => stream.TranscriptIdentity,
                    stream => stream.SourceStream,
                    StringComparer.Ordinal);
            return CodexTranscriptDiscovery
                .EnumerateCurrentSessionsAndResponsibleArchives(
                    transcriptRoot,
                    archiveRoot!,
                    responsibleSourceStreamsByTranscriptIdentity);
        }

        IReadOnlyList<CodexTranscriptStream>? streams = null;

        CaptureInstructionPoll? instructionPoll = null;
        Exception? instructionPollFailure = null;
        try
        {
            CaptureInstructionPoll candidateInstructionPoll =
                await PollCaptureInstructionsAsync(
                endpoint, credential, cancellationToken);
            await PersistInstructionPolicyAsync(
                stateDirectory, candidateInstructionPoll.Paused, cancellationToken);
            instructionPoll = candidateInstructionPoll;
        }
        catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
        {
            // An unavailable policy server cannot authorize delivery. Local
            // claiming may continue so durable queue responsibility is not
            // discarded during an outage.
            instructionPollFailure = ex;
        }

        bool paused = instructionPoll?.Paused ?? persistedPaused ?? false;
        if (paused)
        {
            if (instructionPollFailure is not null)
            {
                WriteFailure(instructionPollFailure);
            }
            WriteDiagnostic("capture_policy_paused");
            if (instructionPoll is not null)
            {
                try
                {
                    await AcknowledgeCaptureInstructionsAsync(
                        endpoint,
                        credential,
                        instructionPoll.Instructions
                            .Where(instruction =>
                                instruction.Operation is CaptureInstructionOperations.Pause
                                    or CaptureInstructionOperations.Resume)
                            .ToArray(),
                        cancellationToken);
                }
                catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
                {
                    WriteFailure(ex);
                }
            }
            return;
        }
        if (streams is null)
        {
            try
            {
                streams = DiscoverStreams();
            }
            catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
            {
                WriteFailure(ex);
                return;
            }
        }
        bool compatibleWorkCompleted = true;
        bool filesystemWorkCompleted = await CodexTranscriptScanCycle.RunAsync(
            streams,
            async (transcript, token) =>
            {
                try
                {
                    await ScanAndDeliverAsync(
                        transcript, token, deliveryAuthorized: instructionPoll is not null);
                }
                catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
                {
                    // One source stream or endpoint outage cannot cancel
                    // responsibility for later cycles/streams.
                    compatibleWorkCompleted = false;
                    WriteFailure(ex);
                }
            },
            WriteFailure,
            cancellationToken);
        compatibleWorkCompleted &= filesystemWorkCompleted;
        if (instructionPollFailure is not null)
        {
            WriteFailure(instructionPollFailure);
        }
        if (instructionPoll is not null)
        {
            try
            {
                await AcknowledgeCaptureInstructionsAsync(
                    endpoint,
                    credential,
                    instructionPoll.Instructions
                        .Where(instruction => compatibleWorkCompleted
                            || instruction.Operation is CaptureInstructionOperations.Pause
                                or CaptureInstructionOperations.Resume)
                        .ToArray(),
                    cancellationToken);
            }
            catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
            {
                WriteFailure(ex);
            }
        }
    }

    await CaptureRescanScheduler.RunAsync(
        ScanCycleAsync,
        schedule,
        cancellationToken: stopping.Token);
}
catch (OperationCanceledException) when (stopping.IsCancellationRequested)
{
}
catch (Exception ex) when (IsExpectedRuntimeFailure(ex))
{
    WriteFailure(ex);
    return 4;
}

WriteDiagnostic("capture_runtime_stopped");
return 0;

static CaptureServerReceiptState ValidateReceipt(
    string receipt,
    CaptureRuntimeQueueItem queued)
{
    JsonDocument parsed;
    try
    {
        parsed = JsonDocument.Parse(receipt);
    }
    catch (JsonException)
    {
        throw InvalidReceipt();
    }
    using JsonDocument document = parsed;
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object
        || HasDuplicatePropertyNames(root)
        || !TryGetInt64(root, "sourcePosition", out long receiptSourcePosition)
        || receiptSourcePosition < 0
        || receiptSourcePosition != queued.SourcePosition
        || !TryGetString(root, "status", out string? status)
        || status is not ("new" or "already_accepted")
        || !TryGetNonemptyGuid(root, "observationUuid", out Guid observationUuid)
        || !root.TryGetProperty("observation", out JsonElement observation)
        || observation.ValueKind != JsonValueKind.Object
        || !TryGetNonemptyGuid(
            observation, "observationUuid", out Guid nestedObservationUuid)
        || nestedObservationUuid != observationUuid
        || !TryGetNonemptyGuid(
            observation, "sourceStreamUuid", out Guid sourceStreamUuid)
        || !observation.TryGetProperty("locator", out JsonElement receiptLocator)
        || receiptLocator.ValueKind != JsonValueKind.Object
        || !TryGetString(receiptLocator, "kind", out string? locatorKind)
        || locatorKind != "byte_range"
        || !TryGetInt64(receiptLocator, "byteOffset", out long byteOffset)
        || byteOffset < 0
        || byteOffset != queued.DeterministicLocatorEvidence.ByteOffset
        || !TryGetInt64(receiptLocator, "byteLength", out long byteLength)
        || byteLength <= 0
        || byteLength != queued.DeterministicLocatorEvidence.ByteLength
        || (root.TryGetProperty("sourceStreamUuid", out JsonElement topSourceStreamUuid)
            && (!TryReadNonemptyGuid(topSourceStreamUuid, out Guid topStreamUuid)
                || topStreamUuid != sourceStreamUuid)))
    {
        throw InvalidReceipt();
    }

    return new CaptureServerReceiptState(
        receiptSourcePosition,
        queued.DeterministicLocatorEvidence.Identity,
        status,
        observationUuid,
        sourceStreamUuid);
}

static bool HasDuplicatePropertyNames(JsonElement value)
{
    if (value.ValueKind == JsonValueKind.Array)
    {
        return value.EnumerateArray().Any(HasDuplicatePropertyNames);
    }
    if (value.ValueKind != JsonValueKind.Object)
    {
        return false;
    }

    var names = new HashSet<string>(StringComparer.Ordinal);
    foreach (JsonProperty property in value.EnumerateObject())
    {
        if (!names.Add(property.Name) || HasDuplicatePropertyNames(property.Value))
        {
            return true;
        }
    }
    return false;
}

static bool TryGetInt64(JsonElement parent, string propertyName, out long value)
{
    value = default;
    return parent.TryGetProperty(propertyName, out JsonElement element)
        && element.ValueKind == JsonValueKind.Number
        && element.TryGetInt64(out value);
}

static bool TryGetString(JsonElement parent, string propertyName, out string? value)
{
    value = null;
    return parent.TryGetProperty(propertyName, out JsonElement element)
        && element.ValueKind == JsonValueKind.String
        && (value = element.GetString()) is not null;
}

static bool TryGetNonemptyGuid(
    JsonElement parent,
    string propertyName,
    out Guid value)
{
    value = default;
    return parent.TryGetProperty(propertyName, out JsonElement element)
        && TryReadNonemptyGuid(element, out value);
}

static bool TryReadNonemptyGuid(JsonElement element, out Guid value)
{
    value = default;
    return element.ValueKind == JsonValueKind.String
        && element.TryGetGuid(out value)
        && value != Guid.Empty;
}

static InvalidDataException InvalidReceipt() =>
    new("Capture server receipt has an unsupported contract.");

static void WriteFailure(Exception failure)
{
    WriteDiagnostic("capture_cycle_failed", FailureCode(failure));
    CaptureOutcomeSummary? outcome = failure switch
    {
        SafetyConfigurationException configuration => configuration.Outcome,
        SafetyScanException scan => scan.Outcome,
        _ => null
    };
    if (outcome is not null)
    {
        WriteOutcome(outcome);
    }
}

static string FailureCode(Exception failure) => failure switch
{
    DirectoryNotFoundException => "transcript_root_unavailable",
    FileNotFoundException => "transcript_stream_unavailable",
    UnauthorizedAccessException => "transcript_access_denied",
    IOException => "transcript_io_unavailable",
    CaptureDeliveryException => "delivery_failed",
    HttpRequestException => "endpoint_unavailable",
    CapturePrefixChangedException => "verified_prefix_changed",
    CaptureStreamStoppedException => "stream_stopped",
    CaptureRuntimeConcurrencyException => "state_concurrency",
    InvalidDataException => "invalid_source_or_receipt",
    JsonException => "invalid_json",
    SafetyScanException => "safety_scan_failed",
    SafetyConfigurationException => "safety_configuration_failed",
    InvalidOperationException => "invalid_configuration_or_state",
    ArgumentException => "invalid_configuration_or_state",
    NotSupportedException => "unsupported_configuration_or_state",
    System.Security.Authentication.AuthenticationException => "authentication_failed",
    _ => "scan_failed"
};

static bool IsExpectedFilesystemFailure(Exception failure) =>
    failure is IOException or UnauthorizedAccessException;

static bool IsExpectedRuntimeFailure(Exception failure) =>
    failure is not OperationCanceledException
    && (failure is CaptureDeliveryException
        or HttpRequestException
        or CapturePrefixChangedException
        or CaptureStreamStoppedException
        or CaptureRuntimeConcurrencyException
        or InvalidDataException
        or JsonException
        or SafetyScanException
        or SafetyConfigurationException
        or InvalidOperationException
        or ArgumentException
        or NotSupportedException
        or System.Security.Authentication.AuthenticationException
        || IsExpectedFilesystemFailure(failure));

static void WriteDiagnostic(string eventName, string? reason = null) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        new { @event = eventName, adapter = "codex", reason },
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static void WriteOutcome(CaptureOutcomeSummary outcome) =>
    Console.Error.WriteLine(JsonSerializer.Serialize(
        outcome,
        new JsonSerializerOptions(JsonSerializerDefaults.Web)));

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required.");

static async Task<string> ResolveCredentialAsync(string endpoint, string stateDirectory)
{
    string? configured = Environment.GetEnvironmentVariable("OVERMIND_CAPTURE_CREDENTIAL");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        if (!CaptureCredential.IsCaptureForm(configured))
            throw new InvalidOperationException(
                "OVERMIND_CAPTURE_CREDENTIAL must be a restricted capture credential.");
        return configured;
    }

    Directory.CreateDirectory(stateDirectory);
    string credentialPath = Path.Combine(stateDirectory, "capture-credential");
    if (File.Exists(credentialPath))
    {
        string persisted = (await File.ReadAllTextAsync(credentialPath)).Trim();
        if (!CaptureCredential.IsCaptureForm(persisted))
            throw new InvalidDataException("Persisted capture credential is invalid.");
        return persisted;
    }

    string installationPath = Path.Combine(stateDirectory, "codex-installation-id");
    string installationId;
    if (File.Exists(installationPath))
    {
        installationId = (await File.ReadAllTextAsync(installationPath)).Trim();
        if (string.IsNullOrWhiteSpace(installationId))
            throw new InvalidDataException("Persisted Codex installation identity is invalid.");
    }
    else
    {
        installationId = Guid.NewGuid().ToString("N");
        await WritePrivateFileAsync(installationPath, installationId);
    }

    using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/") };
    using HttpResponseMessage created = await client.PostAsJsonAsync(
        "capture/v1/pairing-requests",
        new { machineName = Environment.MachineName, codexInstallationId = installationId });
    created.EnsureSuccessStatusCode();
    PairingCreated? pairing = await created.Content.ReadFromJsonAsync<PairingCreated>();
    if (pairing is null
        || pairing.RequestId == Guid.Empty
        || string.IsNullOrWhiteSpace(pairing.VerificationUri)
        || string.IsNullOrWhiteSpace(pairing.UserCode)
        || string.IsNullOrWhiteSpace(pairing.PollingToken))
        throw new InvalidDataException("Pairing server response is invalid.");

    // The URL and user code are explicitly non-secret. Neither the polling
    // capability nor the delivered credential enters diagnostics.
    Console.Error.WriteLine(JsonSerializer.Serialize(new
    {
        @event = "capture_pairing_required",
        adapter = "codex",
        verificationUri = pairing.VerificationUri,
        userCode = pairing.UserCode
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    while (true)
    {
        using var pollRequest = new HttpRequestMessage(
            HttpMethod.Get, $"capture/v1/pairing-requests/{pairing.RequestId}");
        pollRequest.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", pairing.PollingToken);
        using HttpResponseMessage response = await client.SendAsync(pollRequest);
        if (response.StatusCode == System.Net.HttpStatusCode.Gone)
            throw new InvalidOperationException("Capture pairing is no longer available.");
        response.EnsureSuccessStatusCode();
        PairingPoll? result = await response.Content.ReadFromJsonAsync<PairingPoll>();
        if (result?.Status == "approved")
        {
            if (!CaptureCredential.IsCaptureForm(result.Credential ?? ""))
                throw new InvalidDataException("Pairing credential response is invalid.");
            await WritePrivateFileAsync(credentialPath, result.Credential!);
            WriteDiagnostic("capture_pairing_completed");
            return result.Credential!;
        }
        if (result?.Status != "pending")
            throw new InvalidDataException("Pairing poll response is invalid.");
        await Task.Delay(TimeSpan.FromSeconds(2));
    }
}

static async Task WritePrivateFileAsync(string path, string content)
{
    await using var stream = new FileStream(path, new FileStreamOptions
    {
        Access = FileAccess.Write,
        Mode = FileMode.CreateNew,
        Share = FileShare.None,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    });
    await using var writer = new StreamWriter(stream, leaveOpen: false);
    await writer.WriteAsync(content);
    await writer.FlushAsync();
}

static async Task<CaptureInstructionPoll> PollCaptureInstructionsAsync(
    string endpoint,
    string credential,
    CancellationToken cancellationToken)
{
    using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/") };
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
    using HttpResponseMessage response = await client.GetAsync(
        "capture/v1/instructions", cancellationToken);
    response.EnsureSuccessStatusCode();
    CaptureInstructionPoll? poll =
        await response.Content.ReadFromJsonAsync<CaptureInstructionPoll>(cancellationToken);
    if (poll?.Instructions is null)
        throw new InvalidDataException("Capture instruction poll response is invalid.");
    foreach (CaptureInstruction instruction in poll.Instructions)
    {
        if (instruction.InstructionId == Guid.Empty)
            throw new InvalidDataException("Capture instruction identity is invalid.");
        _ = CaptureInstructionOperations.Require(instruction.Operation);
    }
    return poll;
}

static async Task AcknowledgeCaptureInstructionsAsync(
    string endpoint,
    string credential,
    IReadOnlyList<CaptureInstruction> instructions,
    CancellationToken cancellationToken)
{
    using var client = new HttpClient { BaseAddress = new Uri(endpoint + "/") };
    client.DefaultRequestHeaders.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential);
    foreach (CaptureInstruction instruction in instructions)
    {
        using HttpResponseMessage response = await client.PostAsync(
            $"capture/v1/instructions/{instruction.InstructionId}/acknowledge",
            content: null,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        CaptureInstructionAcknowledgement? acknowledgement =
            await response.Content.ReadFromJsonAsync<CaptureInstructionAcknowledgement>(
                cancellationToken);
        if (acknowledgement is null
            || acknowledgement.InstructionId != instruction.InstructionId
            || acknowledgement.AcknowledgedAt == default)
        {
            throw new InvalidDataException(
                "Capture instruction acknowledgement response is invalid.");
        }
    }
}

static async Task PersistInstructionPolicyAsync(
    string stateDirectory,
    bool paused,
    CancellationToken cancellationToken)
{
    Directory.CreateDirectory(stateDirectory);
    string path = Path.Combine(stateDirectory, "capture-instruction-policy.json");
    string temporary = Path.Combine(
        stateDirectory, $"capture-instruction-policy-{Guid.NewGuid():N}.tmp");
    string serialized = JsonSerializer.Serialize(
        new { paused }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    try
    {
        await File.WriteAllTextAsync(temporary, serialized, cancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
    }
    finally
    {
        if (File.Exists(temporary)) File.Delete(temporary);
    }
}

static async Task<bool?> LoadInstructionPolicyAsync(
    string stateDirectory,
    CancellationToken cancellationToken)
{
    string path = Path.Combine(stateDirectory, "capture-instruction-policy.json");
    if (!File.Exists(path))
    {
        return null;
    }

    await using FileStream stream = File.OpenRead(path);
    using JsonDocument document = await JsonDocument.ParseAsync(
        stream, cancellationToken: cancellationToken);
    JsonElement root = document.RootElement;
    if (root.ValueKind != JsonValueKind.Object)
    {
        throw new InvalidDataException("Capture instruction policy is invalid.");
    }
    JsonProperty[] properties = root.EnumerateObject().ToArray();
    if (properties.Length != 1
        || properties[0].Name != "paused"
        || properties[0].Value.ValueKind is not JsonValueKind.True
            and not JsonValueKind.False)
    {
        throw new InvalidDataException("Capture instruction policy is invalid.");
    }
    return properties[0].Value.GetBoolean();
}

sealed record PairingCreated(
    Guid RequestId, string VerificationUri, string UserCode,
    string PollingToken, DateTimeOffset ExpiresAt);
sealed record PairingPoll(string Status, string? Credential);
