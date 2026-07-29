-- Capture stream identity is the authenticated binding plus an explicit
-- harness-native tuple. Parent and fork relationship facts are not identity.
ALTER TABLE capture_source_streams
  ADD COLUMN external_session_id TEXT,
  ADD COLUMN child_id TEXT,
  ADD COLUMN trace_session_id TEXT;

-- A pre-0008 Codex rollout was keyed by its discovered path/filename even when
-- its accepted session_meta record already carried the harness-native tuple.
-- Recover that tuple from the immutable, safety-scanned observation. Invalid or
-- contradictory classifiers deliberately fall through to the legacy identity.
WITH metadata AS (
  SELECT DISTINCT ON (o.stream_uuid)
         o.stream_uuid,
         NULLIF(btrim(COALESCE(
           o.safe_source_payload->'payload'->>'session_id',
           o.safe_source_payload->'payload'->>'id')), '') AS external_session_id,
         NULLIF(btrim(o.safe_source_payload->'payload'->>'id'), '') AS thread_id,
         CASE
           WHEN NOT (o.safe_source_payload->'payload' ? 'source') THEN NULL
           WHEN jsonb_typeof(o.safe_source_payload->'payload'->'source') = 'string'
             THEN lower(o.safe_source_payload->'payload'->>'source') = 'subagent'
           WHEN jsonb_typeof(o.safe_source_payload->'payload'->'source') = 'object'
             THEN CASE
               WHEN (o.safe_source_payload->'payload'->'source' ? 'subagent')
                 OR (o.safe_source_payload->'payload'->'source' ? 'sub_agent')
                 THEN true
               WHEN (o.safe_source_payload->'payload'->'source' ? 'internal')
                 OR (o.safe_source_payload->'payload'->'source' ? 'custom')
                 THEN false
               ELSE NULL
             END
           ELSE NULL
         END AS source_class,
         CASE
           WHEN NOT (o.safe_source_payload->'payload' ? 'thread_source') THEN NULL
           WHEN jsonb_typeof(o.safe_source_payload->'payload'->'thread_source') = 'string'
             THEN lower(o.safe_source_payload->'payload'->>'thread_source') = 'subagent'
           ELSE NULL
         END AS thread_class
  FROM capture_observations o
  WHERE o.source->>'harness' = 'codex'
    AND o.source->>'recordType' = 'session_meta'
    AND jsonb_typeof(o.safe_source_payload->'payload') = 'object'
  ORDER BY o.stream_uuid, o.source_position, o.observation_uuid
),
valid_metadata AS (
  SELECT stream_uuid,
         external_session_id,
         CASE
           WHEN source_class IS TRUE OR thread_class IS TRUE THEN thread_id
           ELSE NULL
         END AS child_id
  FROM metadata
  WHERE external_session_id IS NOT NULL
    AND NOT (
      source_class IS NOT NULL
      AND thread_class IS NOT NULL
      AND source_class <> thread_class)
    AND (
      (source_class IS NOT TRUE AND thread_class IS NOT TRUE)
      OR thread_id IS NOT NULL)
)
UPDATE capture_source_streams s
SET external_session_id = m.external_session_id,
    child_id = m.child_id
FROM valid_metadata m
WHERE m.stream_uuid = s.stream_uuid;

UPDATE capture_source_streams
SET external_session_id = source_session_id
WHERE external_session_id IS NULL;

-- A stream's canonical trace-session identity is already historical fact once
-- any event has been accepted. Preserve it and let all later appends reuse it.
-- Empty legacy streams retain the pre-0008 derivation they would have used.
UPDATE capture_source_streams s
SET trace_session_id = COALESCE(
  (
    SELECT e.session_id
    FROM capture_observations o
    JOIN captured_events e USING (observation_uuid)
    WHERE o.stream_uuid = s.stream_uuid
    ORDER BY o.source_position, e.part_order, e.trace_uuid
    LIMIT 1
  ),
  'capture:' || s.binding_uuid::text || ':' || s.source_session_id);

ALTER TABLE capture_source_streams
  ALTER COLUMN external_session_id SET NOT NULL,
  ALTER COLUMN trace_session_id SET NOT NULL,
  DROP CONSTRAINT capture_source_streams_binding_uuid_source_session_id_key,
  ADD CONSTRAINT capture_source_streams_binding_external_child_unique
    UNIQUE NULLS NOT DISTINCT (binding_uuid, external_session_id, child_id);
