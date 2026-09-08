namespace MemSrv.Core;

public sealed partial class MemoryService
{
    public Task<ToolEnvelope<IReadOnlyList<WorkstreamRecord>>> ListWorkstreamsAsync(
        MemoryContext context,
        string? @namespace = null,
        string? status = null,
        CancellationToken cancellationToken = default) =>
        _workstreams.ListWorkstreamsAsync(context, @namespace, status, cancellationToken);

    public Task<ToolEnvelope<WorkstreamCheckoutResult>> CheckoutWorkstreamAsync(
        MemoryContext context,
        Guid? uuid,
        string? title,
        CancellationToken cancellationToken = default) =>
        _workstreams.CheckoutWorkstreamAsync(context, uuid, title, cancellationToken);

    public Task<ToolEnvelope<WorkstreamRecord>> CheckinWorkstreamAsync(
        MemoryContext context,
        Guid uuid,
        string status,
        string notes,
        Guid[]? refs = null,
        CancellationToken cancellationToken = default) =>
        _workstreams.CheckinWorkstreamAsync(context, uuid, status, notes, refs, cancellationToken);

    public Task<ToolEnvelope<WorkstreamRecord>> CreateHandoffAsync(
        MemoryContext context,
        string? @namespace,
        string summary,
        Guid[] refs,
        CancellationToken cancellationToken = default) =>
        _workstreams.CreateHandoffAsync(context, @namespace, summary, refs, cancellationToken);

    /// <summary>
    /// Operator escape hatch (memctl release): flips a stale checked-out
    /// workstream back to open, clearing owner and session but keeping the
    /// notes. Unlike the agent-facing checkin, it writes no trace event: the
    /// taxonomy's workstream events describe agent coordination, and the
    /// binding spec defines no operator-release event convention.
    /// </summary>
    public Task<WorkstreamRecord> ReleaseWorkstreamAsync(Guid uuid, CancellationToken cancellationToken = default) =>
        _workstreams.ReleaseWorkstreamAsync(uuid, cancellationToken);
}
