-- Remediate the disabled capture tracer bullet without widening its surface.
-- Stream routing is fixed on first import, checkpoints are numeric contiguous
-- prefixes, and retry signatures are keyed with binding-owned random material.
ALTER TABLE capture_source_bindings
  ADD COLUMN content_signature_key BYTEA NOT NULL DEFAULT gen_random_bytes(32);

ALTER TABLE capture_source_streams
  ADD COLUMN effective_namespace TEXT REFERENCES namespaces(name),
  ADD COLUMN route_basis TEXT CHECK (route_basis IN ('configured_binding', 'fallback')),
  ADD COLUMN checkpoint_position BIGINT;

UPDATE capture_source_streams s
SET effective_namespace = COALESCE(b.route_namespace, 'capture/unscoped'),
    route_basis = CASE WHEN b.route_namespace IS NULL THEN 'fallback' ELSE 'configured_binding' END
FROM capture_source_bindings b
WHERE b.binding_uuid = s.binding_uuid;

ALTER TABLE capture_source_streams
  ALTER COLUMN effective_namespace SET NOT NULL,
  ALTER COLUMN route_basis SET NOT NULL;

ALTER TABLE capture_observations
  ADD COLUMN source_position BIGINT,
  ADD COLUMN content_signature TEXT,
  ADD COLUMN scan_rule_set_version TEXT NOT NULL DEFAULT 'legacy',
  ADD COLUMN scan_rule_ids TEXT[] NOT NULL DEFAULT '{}',
  ADD COLUMN scan_categories TEXT[] NOT NULL DEFAULT '{}',
  ADD COLUMN scan_redaction_count INTEGER NOT NULL DEFAULT 0;

WITH positioned AS (
  SELECT observation_uuid,
         row_number() OVER (PARTITION BY stream_uuid ORDER BY captured_at, observation_uuid) - 1 AS position
  FROM capture_observations
)
UPDATE capture_observations o
SET source_position = p.position
FROM positioned p
WHERE p.observation_uuid = o.observation_uuid;

UPDATE capture_observations o
SET content_signature = encode(
      hmac(convert_to(o.content_hash, 'UTF8'), b.content_signature_key, 'sha256'),
      'hex')
FROM capture_source_streams s
JOIN capture_source_bindings b USING (binding_uuid)
WHERE s.stream_uuid = o.stream_uuid;

UPDATE capture_source_streams s
SET checkpoint_position = accepted.position
FROM (
  SELECT stream_uuid, max(source_position) AS position
  FROM capture_observations
  GROUP BY stream_uuid
) accepted
WHERE accepted.stream_uuid = s.stream_uuid;

ALTER TABLE capture_observations
  ALTER COLUMN source_position SET NOT NULL,
  ALTER COLUMN content_signature SET NOT NULL,
  DROP COLUMN content_hash,
  ADD CONSTRAINT capture_observations_stream_position_unique
    UNIQUE (stream_uuid, source_position);

ALTER TABLE capture_source_streams DROP COLUMN checkpoint_locator;
