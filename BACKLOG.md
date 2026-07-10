# Backlog

## Open

### [DES-001] Decide whether retirement belongs in the trace event taxonomy
- **Category**: design
- **Location**: `src/MemSrv.Core/MemoryService.cs` (`RetireAsync`); spec §191 event taxonomy
- **Context**: Slice 5's `memctl retire` is a bare status flip with no trace event, unlike approve/reject which log review events. Deliberate for the slice (retirement is not in the event taxonomy and not an adjudication), but leaves no ledger record of who retired a memory and when beyond `retired_at`. Decide whether a retirement event type should exist; if yes, it's a taxonomy change, not a bugfix.
- **Added**: 2026-07-10
- **Status**: open

### [DES-002] log_trace session_id should default to the transport session in HTTP mode
- **Category**: design
- **Location**: `src/MemSrv.Server/McpMemoryTools.cs` (log_trace), `src/MemSrv.Core/MemoryService.cs` (LogTraceAsync)
- **Context**: `log_trace` takes a required `session_id` and stores the agent-supplied value. Per PRD #2 the explicit `session_id` is an import override and only server-side auto-logging is transport-scoped — but a normal HTTP agent calling `log_trace` can fragment its own trace across session ids vs the server's auto-logged events (`memory_consumed`, etc.). Proposed fix: make `session_id` optional, defaulting to `context.SessionId`, keeping the explicit value as an import override. Changes an existing tool contract, so deferred out of #7. Flagged by the Slice 3 (#7) spec review.
- **Added**: 2026-07-10
- **Status**: open

## In Progress

## Done
