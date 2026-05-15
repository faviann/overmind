CREATE TABLE IF NOT EXISTS schema_migrations (
    version TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS namespaces (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS memory_proposals (
    id TEXT PRIMARY KEY,
    namespace_id TEXT NOT NULL REFERENCES namespaces(id),
    memory_type TEXT NOT NULL,
    content TEXT NOT NULL,
    source_text TEXT,
    rationale TEXT,
    status TEXT NOT NULL DEFAULT 'pending'
        CHECK (status IN ('pending', 'approved', 'rejected')),
    reviewed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS memory_proposals_namespace_status_idx
    ON memory_proposals(namespace_id, status, created_at);

CREATE INDEX IF NOT EXISTS memory_proposals_namespace_type_idx
    ON memory_proposals(namespace_id, memory_type);

CREATE TABLE IF NOT EXISTS knowledge_entries (
    id TEXT PRIMARY KEY,
    namespace_id TEXT NOT NULL REFERENCES namespaces(id),
    proposal_id TEXT NOT NULL UNIQUE REFERENCES memory_proposals(id),
    memory_type TEXT NOT NULL,
    title TEXT,
    content TEXT NOT NULL,
    source_text TEXT,
    approved_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS knowledge_entries_namespace_type_idx
    ON knowledge_entries(namespace_id, memory_type, approved_at DESC);

CREATE INDEX IF NOT EXISTS knowledge_entries_content_text_idx
    ON knowledge_entries USING gin (to_tsvector('simple', coalesce(title, '') || ' ' || content));
