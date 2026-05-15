CREATE TABLE IF NOT EXISTS trace_events (
    id TEXT PRIMARY KEY,
    session_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    event_type TEXT NOT NULL
        CHECK (event_type IN (
            'user_message',
            'agent_message',
            'tool_call',
            'tool_result',
            'command_run',
            'file_observed',
            'file_modified',
            'decision',
            'error',
            'memory_proposal_created',
            'memory_approved',
            'memory_rejected',
            'memory_superseded'
        )),
    occurred_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    content TEXT NOT NULL,
    metadata JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS trace_events_session_idx
    ON trace_events(session_id, occurred_at ASC);

CREATE INDEX IF NOT EXISTS trace_events_project_idx
    ON trace_events(project_id, occurred_at ASC);

ALTER TABLE memory_proposals
    ADD COLUMN IF NOT EXISTS source_event_id TEXT REFERENCES trace_events(id);

CREATE INDEX IF NOT EXISTS memory_proposals_source_event_idx
    ON memory_proposals(source_event_id);

ALTER TABLE knowledge_entries
    ADD COLUMN IF NOT EXISTS source_event_id TEXT REFERENCES trace_events(id);

CREATE INDEX IF NOT EXISTS knowledge_entries_source_event_idx
    ON knowledge_entries(source_event_id);
