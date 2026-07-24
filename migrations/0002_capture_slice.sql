-- Narrow Phase-2 capture slice. Capture ledger rows are immutable; only a
-- stream checkpoint and operator-owned binding metadata may change.
INSERT INTO namespaces (name, description)
VALUES ('capture/unscoped', 'Fallback for capture with no configured semantic route')
ON CONFLICT (name) DO NOTHING;

CREATE TABLE capture_source_bindings (
  binding_uuid       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  stable_name        TEXT NOT NULL UNIQUE,
  harness            TEXT NOT NULL,
  agent_id           TEXT NOT NULL,
  credential_hash    TEXT NOT NULL UNIQUE,
  route_namespace    TEXT REFERENCES namespaces(name),
  allowed_namespaces TEXT[] NOT NULL,
  active              BOOLEAN NOT NULL DEFAULT true,
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE capture_source_streams (
  stream_uuid        UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  binding_uuid       UUID NOT NULL REFERENCES capture_source_bindings(binding_uuid),
  source_session_id  TEXT NOT NULL,
  checkpoint_locator TEXT,
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (binding_uuid, source_session_id)
);

CREATE TABLE capture_observations (
  observation_uuid    UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  stream_uuid         UUID NOT NULL REFERENCES capture_source_streams(stream_uuid),
  source_locator      TEXT NOT NULL,
  content_hash        TEXT NOT NULL,
  effective_namespace TEXT NOT NULL REFERENCES namespaces(name),
  route_basis         TEXT NOT NULL CHECK (route_basis IN ('configured_binding', 'fallback')),
  source              JSONB NOT NULL,
  adapter             JSONB NOT NULL,
  safe_source_payload JSONB NOT NULL,
  scan_status         TEXT NOT NULL,
  captured_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (stream_uuid, source_locator)
);

CREATE TABLE captured_events (
  trace_uuid       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  observation_uuid UUID NOT NULL REFERENCES capture_observations(observation_uuid),
  session_id       TEXT NOT NULL,
  agent_id         TEXT NOT NULL,
  namespace        TEXT NOT NULL REFERENCES namespaces(name),
  part_key         TEXT NOT NULL,
  part_order       INTEGER NOT NULL,
  kind             TEXT NOT NULL,
  actor            TEXT NOT NULL,
  occurred_at      TIMESTAMPTZ,
  payload_version  INTEGER NOT NULL DEFAULT 1,
  payload          JSONB NOT NULL,
  UNIQUE (observation_uuid, part_key),
  UNIQUE (observation_uuid, part_order)
);

CREATE TABLE captured_event_relationships (
  relationship_uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  source_trace_uuid UUID NOT NULL REFERENCES captured_events(trace_uuid),
  relationship_type TEXT NOT NULL,
  target_native_id  TEXT NOT NULL,
  target_kind       TEXT,
  UNIQUE (source_trace_uuid, relationship_type, target_native_id, target_kind)
);

CREATE TRIGGER capture_observations_immutable
BEFORE UPDATE OR DELETE ON capture_observations
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
CREATE TRIGGER captured_events_immutable
BEFORE UPDATE OR DELETE ON captured_events
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();
CREATE TRIGGER captured_event_relationships_immutable
BEFORE UPDATE OR DELETE ON captured_event_relationships
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

GRANT SELECT, INSERT, UPDATE ON capture_source_bindings, capture_source_streams TO memsrv;
GRANT SELECT, INSERT ON capture_observations, captured_events, captured_event_relationships TO memsrv;
