# Backlog

## Open

### [DES-001] Decide whether retirement belongs in the trace event taxonomy
- **Category**: design
- **Location**: `src/MemSrv.Core/MemoryService.cs` (`RetireAsync`); spec §191 event taxonomy
- **Context**: Slice 5's `memctl retire` is a bare status flip with no trace event, unlike approve/reject which log review events. Deliberate for the slice (retirement is not in the event taxonomy and not an adjudication), but leaves no ledger record of who retired a memory and when beyond `retired_at`. Decide whether a retirement event type should exist; if yes, it's a taxonomy change, not a bugfix.
- **Added**: 2026-07-10
- **Status**: open

## In Progress

## Done
