-- Requires the memsrv role to already exist. Roles are owned by provisioning
-- (Ansible in production, docker/postgres-init in dev/CI); migrations grant to
-- memsrv but never create roles or manage passwords.
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE TABLE namespaces (
  name         TEXT PRIMARY KEY,
  description  TEXT,
  created_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE traces (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  trace_uuid  UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  session_id  TEXT NOT NULL,
  agent_id    TEXT NOT NULL,
  namespace   TEXT NOT NULL REFERENCES namespaces(name),
  event_type  TEXT NOT NULL,
  content     JSONB NOT NULL,
  refs        UUID[],
  ts          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX idx_traces_session ON traces(session_id, ts);
CREATE INDEX idx_traces_ns_ts ON traces(namespace, ts);

CREATE OR REPLACE FUNCTION forbid_mutation() RETURNS trigger AS $$
BEGIN
  RAISE EXCEPTION 'traces are append-only';
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER traces_immutable BEFORE UPDATE OR DELETE ON traces
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

CREATE TABLE trace_snapshots (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  session_id  TEXT NOT NULL,
  agent_id    TEXT NOT NULL,
  namespace   TEXT NOT NULL REFERENCES namespaces(name),
  snapshot    JSONB NOT NULL,
  summary     TEXT,
  ts          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE memories (
  id            BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  uuid          UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  namespace     TEXT NOT NULL REFERENCES namespaces(name),
  type          TEXT NOT NULL
                CHECK (type IN ('decision','fact','preference','task','adr','runbook','note',
                                'constraint','open_question','warning')),
  visibility    TEXT NOT NULL DEFAULT 'shared'
                CHECK (visibility IN ('private','shared')),
  status        TEXT NOT NULL DEFAULT 'proposed'
                CHECK (status IN ('proposed','approved','rejected','superseded','retired')),
  tier          TEXT NOT NULL DEFAULT 'warm'
                CHECK (tier IN ('hot','warm','cold')),
  content       TEXT NOT NULL,
  content_hash  TEXT NOT NULL,
  metadata      JSONB NOT NULL DEFAULT '{}'::jsonb,
  source_type   TEXT NOT NULL,
  source_id     TEXT,
  agent_id      TEXT NOT NULL,
  session_id    TEXT,
  version       INTEGER NOT NULL DEFAULT 1,
  supersedes    UUID,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  approved_by   TEXT,
  approved_at   TIMESTAMPTZ,
  retired_at    TIMESTAMPTZ,
  search_tsv    tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED
);
CREATE INDEX idx_mem_ns_status ON memories(namespace, status);
CREATE INDEX idx_mem_source ON memories(source_id);
CREATE INDEX idx_mem_hash ON memories(content_hash);
CREATE INDEX idx_mem_tsv ON memories USING GIN (search_tsv);

CREATE TABLE retrieval_config (
  agent_id             TEXT NOT NULL,
  namespace            TEXT NOT NULL,
  lanes                JSONB NOT NULL DEFAULT '["fts","recency"]',
  recency_half_life_h  REAL NOT NULL DEFAULT 720,
  max_results          INTEGER NOT NULL DEFAULT 10,
  preview_chars        INTEGER NOT NULL DEFAULT 200,
  include_proposed     BOOLEAN NOT NULL DEFAULT false,
  PRIMARY KEY (agent_id, namespace)
);

CREATE TABLE workstreams (
  id           BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  uuid         UUID NOT NULL UNIQUE DEFAULT gen_random_uuid(),
  namespace    TEXT NOT NULL REFERENCES namespaces(name),
  title        TEXT NOT NULL,
  status       TEXT NOT NULL DEFAULT 'open'
               CHECK (status IN ('open','checked_out','done','abandoned')),
  owner_agent  TEXT,
  session_id   TEXT,
  notes        TEXT,
  refs         UUID[],
  updated_at   TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE jobs (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  job_type    TEXT NOT NULL,
  payload     JSONB NOT NULL,
  status      TEXT NOT NULL DEFAULT 'pending'
              CHECK (status IN ('pending','running','done','failed')),
  attempts    INTEGER NOT NULL DEFAULT 0,
  created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at  TIMESTAMPTZ
);

INSERT INTO namespaces (name, description) VALUES
  ('memory-system', 'Memory server project and build decisions'),
  ('homelab', 'Homelab infrastructure and operations')
ON CONFLICT (name) DO NOTHING;

INSERT INTO retrieval_config (agent_id, namespace, lanes)
VALUES ('*', '*', '["fts","recency"]'::jsonb)
ON CONFLICT (agent_id, namespace) DO NOTHING;

GRANT USAGE ON SCHEMA public TO memsrv;
GRANT SELECT, INSERT ON traces TO memsrv;
GRANT SELECT, INSERT ON trace_snapshots TO memsrv;
GRANT SELECT, INSERT, UPDATE ON memories, workstreams, jobs, retrieval_config, namespaces TO memsrv;
GRANT USAGE ON ALL SEQUENCES IN SCHEMA public TO memsrv;
