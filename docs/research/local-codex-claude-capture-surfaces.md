# Local Codex and Claude Code capture surfaces

Research for [Inventory local Codex and Claude Code capture surfaces](https://github.com/faviann/overmind/issues/59), checked 2026-07-14.

## Question and method

What supported hooks, transcript records, session and subagent relationships, compaction and instruction signals, tool calls/results/failures, and per-event model/provider fields do current local Codex and Claude Code expose? What format drift must capture adapters tolerate?

This note uses only first-party evidence: the current Codex manual and OpenAI's `openai/codex` source, Anthropic's current Claude Code documentation, and structure-only inspection of local transcripts. Local inspection enumerated field names, JSON types, discriminator values, and relationship consistency; it did not copy prompt, response, reasoning, tool-input, or tool-output content. The installed versions were Codex CLI 0.144.3 and Claude Code 2.1.201. Older local records were sampled only to identify drift.

## Answer in brief

- **Both harnesses have usable near-real-time lifecycle hooks and continuously persisted JSONL, but both explicitly disclaim transcript-schema stability.** Hooks should enqueue lightweight capture hints; a version-aware catch-up reader should establish eventual completeness.
- **Claude Code exposes the richer capture-specific hook surface.** It distinguishes successful and failed tools, API-level turn failure, instruction-file loads, pre/post compaction, and subagent start/stop. Codex exposes successful `PostToolUse`, turn stop/abort records, pre/post compaction, and subagent lifecycle, but no dedicated failed-tool, failed-turn, or instruction-file-loaded hook.
- **Neither harness supplies model and provider on every durable event.** Codex records provider at session level and model at turn level; Claude records model on assistant/API messages and in selected hooks/telemetry, while ordinary transcripts have no provider field. Adapters must carry forward only the last explicitly observed value and leave unavailable values unknown.
- **Subagents can be separate Overmind trace sessions without inventing relationships.** Codex child rollouts have their own session/thread identity plus `parent_thread_id`. Claude subagent transcripts are separate nested files with `agent_id`, while their `sessionId` remains the parent's; the supported `SubagentStop` hook supplies both main and agent transcript paths.
- **The durable formats contain duplicate views of some activity.** Codex rollouts mix model-facing `response_item` records with UI/lifecycle `event_msg` records; Claude transcripts mix messages with system/metadata entries. Normalization must choose canonical message/tool records and treat lifecycle entries as annotations, not ingest every line as a distinct conversational event.

## Surface matrix

| Concern | Codex CLI | Claude Code |
|---|---|---|
| Live lifecycle | Command hooks: `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PermissionRequest`, `PostToolUse`, `PreCompact`, `PostCompact`, `SubagentStart`, `SubagentStop`, `Stop` | Hooks include all comparable events plus `PostToolUseFailure`, `StopFailure`, `InstructionsLoaded`, `SessionEnd`, `PermissionDenied`, and others |
| Persisted record | Rollout JSONL under the Codex home `sessions/YYYY/MM/DD` tree | JSONL at `~/.claude/projects/<project>/<session-id>.jsonl` by default |
| Supported structured stream | App-server JSON-RPC: thread/turn/item lifecycle, final statuses, tool outputs, parent thread IDs; version-matched schemas can be generated | `claude -p --output-format json|stream-json` for owned non-interactive runs; Agent SDK for embedded runs |
| User messages | `UserPromptSubmit`; rollout `response_item/message(role=user)` and `event_msg/user_message` | `UserPromptSubmit`; transcript `type=user` |
| Assistant messages | `Stop.last_assistant_message`; rollout `response_item/message(role=assistant)` and `event_msg/agent_message` | `MessageDisplay` text batches and `Stop.last_assistant_message`; transcript `type=assistant` |
| Tool success | `PreToolUse` input and successful `PostToolUse` response; rollout call/output items joined by call ID | `PreToolUse` and `PostToolUse` with input, response, tool-use ID, and optional duration; transcript `tool_use`/`tool_result` blocks |
| Tool failure | No dedicated failure hook; catch-up must read failure-bearing rollout events/results | `PostToolUseFailure` includes error, interruption flag, tool-use ID, and optional duration; transcript tool result may carry `is_error` |
| Turn/API failure | Rollout `event_msg/error` and `turn_aborted`; app-server final turn status is completed/interrupted/failed with structured error | `StopFailure` classifies API error; transcript can contain API-error assistant records |
| Compaction | `PreCompact`/`PostCompact` trigger (`manual` or `auto`); rollout `compacted` summary/window-chain record plus `context_compacted` lifecycle event | `PreCompact` exposes trigger and manual instructions; `PostCompact` exposes generated summary; transcript `system/compact_boundary` carries evolving metrics |
| Instructions | Rollout session metadata has base instructions; model-facing developer messages are persisted. No instruction-load hook | `InstructionsLoaded` reports path, scope, load reason, and dependency path information, but not file content. No hook exposes the complete built-in system prompt |
| Subagents | Hooks expose parent session plus agent ID/type and child transcript path on stop; child rollout has its own identity and `parent_thread_id` | Hooks expose parent session, agent ID/type, and nested agent transcript path; nested transcript retains parent `sessionId`, so `agent_id`/path is required for child identity |
| Model/provider | Hook input has active model; rollout `turn_context.model`; session metadata has optional `model_provider` | `SessionStart.model` is optional; assistant transcript messages have `message.model`; OTEL LLM spans carry model and `gen_ai.system=anthropic`; transcript provider is absent |

## Codex

### Supported live surfaces

Codex hooks run at turn or thread scope. Every hook receives `session_id`, nullable `transcript_path`, `cwd`, event name, and active `model`; turn-scoped inputs also carry `turn_id`, and subagent events carry `agent_id`/`agent_type`. OpenAI warns that `transcript_path` is only a convenience and that the transcript format is not a stable hook interface. [`SessionStart`, tool, compact, prompt, subagent, and stop schemas are defined explicitly in the current manual](https://learn.chatgpt.com/docs/hooks#common-input-fields), and the [0.144.3 schema source](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/hooks/src/schema.rs#L276-L533) gives the exact installed-version wire fields.

The useful capture events are:

- `SessionStart`: session, transcript path, model, permission mode, and source (`startup`, `resume`, `clear`, `compact`).
- `UserPromptSubmit`: prompt and turn ID before model processing.
- `PreToolUse`: tool name, input, and tool-use ID.
- `PostToolUse`: tool name, original input, response, and tool-use ID. In 0.144.3 it runs only after a tool produces a successful output, as stated by the [hook runtime](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/core/src/hook_runtime.rs#L258-L273); there is no `PostToolUseFailure` event in the hook enum.
- `PreCompact` and `PostCompact`: parent session/turn, agent fields where applicable, model, and trigger (`manual` or `auto`). The Codex post hook does **not** provide the generated summary, so catch-up must read the rollout.
- `SubagentStart` and `SubagentStop`: parent session, agent ID/type, model, and turn ID; stop adds `agent_transcript_path` and final assistant text.
- `Stop`: session/turn, model, stop-loop flag, and final assistant text.

Hooks are best-effort capture signals, not the sole ledger source. They can be disabled, untrusted project hooks are skipped, `transcript_path` may be null, and there is no failed-tool hook. Hook commands also run synchronously from the agent loop, so the map's requirement that network ingestion never delay the agent favors a local append-only queue rather than direct network delivery.

Codex app-server is a second, supported surface for clients that own the Codex process. It streams a strict thread → turn → item lifecycle; final `item/completed` records are authoritative, command items include final status/exit code/duration, and `turn/completed` distinguishes completed, interrupted, and failed with structured error details. It also reports immediate `parentThreadId` for subagent threads. App-server can generate TypeScript or JSON Schema from the running CLI, making the schema exact for that installed version. See the [official app-server protocol and schema-generation guide](https://learn.chatgpt.com/docs/app-server#message-schema), the [item/turn lifecycle](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md#turn-events), and the [subagent relationship API](https://github.com/openai/codex/blob/main/codex-rs/app-server/README.md#example-list-descendant-threads). It is higher fidelity than tailing for an observed active thread, but live notifications do not replace scheduled discovery and catch-up of ordinary local CLI rollouts.

### Persisted rollout records

OpenAI's recorder describes rollouts as JSONL intended for replay or inspection and writes them under the Codex home session tree. The exact directory and filename convention is documented in [the 0.144.3 recorder](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/rollout/src/recorder.rs#L1-L75) and [session-list source](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/rollout/src/list.rs#L420-L423). Each line is `{timestamp,type,payload}`. The [rollout discriminator](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/protocol/src/protocol.rs#L3132-L3145) currently includes:

- `session_meta`: session/thread identity, parent/fork relationships, cwd/git metadata, originator, CLI version, source, optional model provider, base instructions, and multi-agent metadata;
- `turn_context`: turn ID, cwd/workspace, permissions, model, collaboration/multi-agent mode, and other effective turn settings;
- `response_item`: model-facing messages, reasoning, tool calls, and tool results;
- `event_msg`: user/assistant UI events, turn start/complete/abort, errors, patch completion, compaction notification, subagent activity, and other lifecycle records;
- `compacted`: the summary/replacement history and optional context-window chain;
- `world_state` and inter-agent communication records.

The model-facing [response item union](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/protocol/src/models.rs#L934-L1160) is the canonical source for conversational messages and call pairing. Messages carry a free-form role (`user`, `assistant`, `developer` occur locally). Calls appear in several variants (`function_call`, `custom_tool_call`, and specialized call types); corresponding outputs use `call_id`. Arguments/input may be JSON encoded inside strings, and outputs may be strings or structured content arrays. Parallel calls make adjacency unsafe: pair by ID.

Tool and turn failures are not one uniform field. Final app-server items have explicit statuses, while rollouts may record a failed/declined command or patch, an `event_msg/error`, a `turn_aborted`, or a model-facing tool output whose text describes failure. The adapter should normalize only explicit status/error evidence; it must not infer success from the existence of an output.

### Session, subagent, compaction, instruction, and model relationships

The [session metadata schema](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/protocol/src/protocol.rs#L3014-L3089) separates `session_id`/thread `id`, `forked_from_id`, and `parent_thread_id`. Each child agent has its own rollout and session identity. `parent_thread_id` is the deterministic immediate-parent edge; `forked_from_id` describes history origin and is not a substitute for the spawn relationship.

Compaction has two complementary records: `event_msg/context_compacted` is only a boundary signal, while `compacted` carries the summary, optional replacement history, and optional `first_window_id`/`previous_window_id`/`window_id`/`window_number`. The [current compatibility deserializer](https://github.com/openai/codex/blob/78ad6e6bfd1d3b6a209acd3ef82172a96b25179c/codex-rs/protocol/src/compacted_item.rs#L1-L50) already accepts an older numeric `window_id`, direct evidence that even this one record has changed shape.

`session_meta.base_instructions` preserves the session's base instruction bundle, and local `response_item/message` records with role `developer` preserve model-visible developer/instruction context. Codex has no `InstructionsLoaded`-equivalent hook, and the manual recommends session logs only as an audit aid, not a stable schema. Capturing the exact loaded instruction-file set therefore remains rollout/log dependent and version sensitive.

Model/provider provenance is hierarchical rather than per event:

- `session_meta.model_provider` is optional and session-scoped;
- `turn_context.model` is the effective model for a turn and must be carried forward only within that turn;
- current hook invocations contain the active model but no provider;
- model reroute events, when present, override the requested model for the affected turn.

### Codex drift observed locally

Structure-only samples spanning CLI 0.77.0 through 0.144.3 showed:

- `session_meta.source` evolving from scalar values such as CLI/IDE/exec origins to a tagged subagent object; `thread_source` evolving from absent/null to explicit user/subagent classification;
- subagent metadata gaining `parent_thread_id`, agent path/nickname/role, and fork metadata;
- `compacted` growing from `{message,replacement_history}` to a six-field context-window chain plus those original fields;
- error records ranging from `error {message,codex_error_info}` to `turn_aborted {reason}` with later optional turn/timing fields;
- new top-level rollout variants and new optional fields inside existing records.

The adapter therefore needs tolerant tagged-union parsing, unknown-field preservation, absent-field defaults, scalar-or-object handling for known migrated fields, and fixtures keyed by `cli_version`.

## Claude Code

### Supported live surfaces

Claude Code's hook contract is broader and is the preferred near-real-time capture surface. Common inputs include session ID, prompt ID (after first user input), transcript path, cwd, permission mode, effort, and event name. Anthropic cautions that the transcript is written asynchronously and can lag the in-memory conversation; final assistant text should come from stop-hook fields rather than immediately reading the file. See [common hook inputs](https://code.claude.com/docs/en/hooks#common-input-fields).

The capture-relevant events are:

- `SessionStart`: source plus optional model, agent type, and title. The model may be absent after clear or recovery, so absence is meaningful, not an invitation to infer it. [SessionStart contract](https://code.claude.com/docs/en/hooks#sessionstart).
- `InstructionsLoaded`: instruction file path, scope (`User`, `Project`, `Local`, or `Managed`), load reason, and optional glob/trigger/parent path. It fires for eager, lazy, included, and post-compaction loads but exposes the path, not file content. [InstructionsLoaded contract](https://code.claude.com/docs/en/hooks#instructionsloaded).
- `UserPromptSubmit`: exact submitted prompt before processing.
- `MessageDisplay`: streamed assistant text with turn/message IDs, batch index, and final marker. It excludes tool-only messages, and its message ID intentionally cannot be correlated with transcript API message IDs. `Stop.last_assistant_message` is the simpler final-answer signal. [MessageDisplay limitations](https://code.claude.com/docs/en/hooks#messagedisplay).
- `PreToolUse`: tool name, typed input, and tool-use ID. `PostToolUse`: original input, typed response, tool-use ID, and optional duration. [Successful tool contract](https://code.claude.com/docs/en/hooks#posttooluse).
- `PostToolUseFailure`: same call identity plus error string, optional interruption flag, and optional duration. [Failed tool contract](https://code.claude.com/docs/en/hooks#posttoolusefailure).
- `PermissionDenied`: denied input and classifier reason. This is distinct from execution failure.
- `StopFailure`: API-level turn error classified as rate limit, overload, authentication, billing, invalid request, missing model, server error, output limit, or unknown. [Turn-failure contract](https://code.claude.com/docs/en/hooks#stopfailure).
- `PreCompact`: `manual`/`auto` trigger and manual custom instructions. `PostCompact`: trigger plus generated compact summary. [Compaction contracts](https://code.claude.com/docs/en/hooks#precompact) and [post-compaction summary](https://code.claude.com/docs/en/hooks#postcompact).
- `SubagentStart`/`SubagentStop`: agent ID/type; stop adds child transcript path and last assistant message. The main transcript path and child transcript path are deliberately separate. [Subagent hook contract](https://code.claude.com/docs/en/hooks#subagentstart).
- `SessionEnd`: exit reason and transcript path, suitable for an archive/catch-up enqueue.

No supported hook exposes Claude Code's complete built-in system prompt. `InstructionsLoaded` precisely inventories external instruction files, and subagent documentation says a subagent's own prompt replaces the default system prompt, but the hook gives only agent type and ID. Full instruction provenance must therefore distinguish content actually observed from file reads/transcripts from unavailable harness-internal system context.

### Persisted transcripts and supported script interfaces

Claude sessions are saved continuously. Anthropic documents three supported structured script paths: `claude -p` JSON/stream-JSON for non-interactive runs, resume-plus-JSON for an existing session, and hook/status-line `transcript_path`; embedded applications should use the Agent SDK. The same page states that raw transcripts live at `~/.claude/projects/<project>/<session-id>.jsonl`, but **each entry's format is internal and may change on any release**. [Claude session data contract](https://code.claude.com/docs/en/sessions#access-conversations-from-scripts).

Current local transcripts use JSON objects rather than one uniform message schema:

- `type=user` and `type=assistant` records form a UUID/`parentUuid` message chain and include session, cwd, branch, timestamp, and harness version metadata;
- assistant `message.content` is an array of text, thinking, and `tool_use` blocks; each assistant API message reports `message.model`;
- user message content carries `tool_result` blocks joined to calls by `tool_use_id`; a result may be string or structured content and may have `is_error`;
- `type=system` covers lifecycle/meta entries such as turn duration, hook summary, local command, away summary, and compact boundary;
- metadata-only types include title, mode, permission mode, prompt pointer, queue operation, attachment, and PR link.

The supported hook contracts are a safer normalization source than these raw shapes. Catch-up still needs the transcript for missed hooks and historical backfill, but it should parse conservatively and retain unrecognized records for a later adapter version.

### Subagent, compaction, instruction, and model relationships

Claude subagents run with isolated context and return results to the parent. The official hook contract stores a child's transcript in a nested `subagents/` folder and sends both the main `transcript_path` and `agent_transcript_path` on stop. [Anthropic's subagent context description](https://code.claude.com/docs/en/sub-agents#manage-subagent-context) explains that ordinary subagents start fresh while forks inherit parent conversation context.

In all structure-only nested samples checked, transcript entries retained the **parent** `sessionId` and added `agentId`; none had a distinct child `sessionId`. Sidecar metadata supplied agent type, description, spawn depth, spawning tool-use ID, and an optional fork flag. Therefore the Overmind trace-session identity for a Claude subagent must be derived from the observed `(parent session, agent ID)` or child transcript path, while the spawning tool-use ID supplies a direct causal link and the parent session remains an explicit relationship. A newer `fork-context-ref` record separately carries parent session/position for forked agents; it should be treated as fork provenance, not assumed present for every subagent.

Compaction is directly observable twice: the hooks provide trigger, manual instructions, and resulting summary; the internal transcript's `system/compact_boundary` provides implementation metrics. The summary is a derived artifact and the pre-compaction transcript remains the primary captured record, consistent with Overmind's trace-first rule.

Claude model/provider evidence is also layered:

- `SessionStart.model` is optional and session-start scoped;
- each assistant transcript API message currently has `message.model`, but this is an internal-schema field;
- detailed OpenTelemetry `claude_code.llm_request` spans report `model`, query source, agent relationships, and `gen_ai.system` as `anthropic`. [Claude Code tracing attributes](https://code.claude.com/docs/en/monitoring-usage#span-attributes);
- ordinary transcript and hook events do not contain a provider/origin field. Without the OTEL span or another explicit harness signal, provider must remain unknown; a Claude-looking model slug is not evidence.

### Claude drift observed locally

Structure-only samples spanning Claude Code 2.1.141 through 2.1.201 showed:

- additive/optional top-level keys (`attributionAgent`, `attributionSkill`, `promptId`, `slug`, `agentId`, and others), plus coexistence of camel-case `sessionId` and optional snake-case `session_id`;
- assistant/user message content as arrays with evolving block types and tool results whose content alternates between string and array;
- `system/compact_boundary` gaining a `compactMetadata` object with trigger, before/after token counts, duration, preserved segments/messages, dropped-token totals, and discovered-tool data;
- older/newer version records coexisting in the same project tree and subagent records differing from main-thread records;
- explicit deprecated fields in current hook docs (for example `team_name`), demonstrating that even supported hooks need versioned fixtures and additive parsing.

## Adapter compatibility requirements surfaced by this inventory

These are format constraints, not an implementation design for the Phase 2 spec:

1. **Stamp source provenance before normalization.** At minimum: harness, harness version, adapter version, source path plus stable local event identity, and explicit model/provider observations. Unknown stays unknown.
2. **Use hook payloads as low-latency hints and persisted records as catch-up truth.** Claude explicitly warns that transcript writes lag hooks; both products warn raw schemas can change.
3. **Make ingestion idempotent at the source-record level.** Hooks and catch-up overlap, and Codex has duplicate `response_item`/`event_msg` views. Pair tool calls/results by source call ID, never adjacency.
4. **Model records as tolerant tagged unions.** Accept unknown record and content-block types, optional fields, scalar/object migrations, string/array outputs, and old/new names. Preserve the source discriminator and an opaque redacted-safe representation when normalization is not yet understood.
5. **Version parsers and fixtures by observed harness version, not one global schema.** Codex app-server schemas should be generated from the installed binary when that surface is used; raw-transcript fixtures should cover every supported version family observed during backfill.
6. **Keep session identity separate from relationship provenance.** Codex child rollouts have their own session plus parent; Claude subagent files reuse the parent session ID and require agent identity/path to mint a distinct trace session. Fork ancestry is not the same as spawn parentage.
7. **Treat compaction as a boundary plus derived summary.** Capture all pre-boundary source events first; record summary/window metrics as additional events, never as a replacement for earlier trace data.
8. **Do not promise complete system-prompt capture.** Codex rollouts expose base/developer instructions but no stable instruction-load event. Claude exposes loaded instruction-file metadata but not the complete built-in or subagent system prompt. The Phase 2 spec should name these as explicit fidelity limits unless a later prototype finds a supported surface.
9. **Test interruption and partial-write recovery.** Catch-up readers must tolerate an actively appended final line, repeated scans, moved/archived sessions, and a process ending before a stop/session-end hook.

## Conclusion

The planned hooks-plus-catch-up shape is supported by the available evidence. Claude Code can emit nearly every normalized capture event directly from hooks, with JSONL filling gaps and historical runs. Codex hooks cover the common path, but complete failure, exact compaction summary, instruction, and missed-event capture depends more heavily on rollouts; app-server is the only supported Codex surface that supplies a strongly typed live item lifecycle and final failure statuses. Both adapters must be explicitly version-aware, and neither can claim a complete model-visible system-prompt record from supported local surfaces alone.
