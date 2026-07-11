namespace MemSrv.Core;

/// <summary>
/// A user-facing workstream coordination failure (checkout conflict, terminal
/// status, non-owner checkin, missing stream). The message is meant for the
/// calling agent: the MCP tool layer re-throws it as an McpException so the
/// text — e.g. which agent and session own a checked-out stream — reaches the
/// client instead of being masked by the SDK's generic tool error.
/// </summary>
public sealed class WorkstreamException(string message) : Exception(message);
