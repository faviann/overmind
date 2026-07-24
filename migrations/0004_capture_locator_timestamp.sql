-- Replace the provisional flat locator with the canonical typed locator and
-- retain source-stated timestamps without deriving either clock from another.
ALTER TABLE capture_observations
  ADD COLUMN locator_kind TEXT,
  ADD COLUMN locator_native_id TEXT,
  ADD COLUMN locator_byte_offset BIGINT,
  ADD COLUMN locator_byte_length BIGINT,
  ADD COLUMN source_timestamp_raw TEXT,
  ADD COLUMN source_timestamp_parsed TIMESTAMPTZ;

-- The existing immutability trigger correctly rejects ordinary updates. The
-- migration owner temporarily suspends it only for this deterministic backfill;
-- DbUp applies the script transactionally and runtime roles cannot disable it.
ALTER TABLE capture_observations
  DISABLE TRIGGER capture_observations_immutable;

UPDATE capture_observations
SET locator_kind = 'native_id',
    locator_native_id = source_locator;

ALTER TABLE capture_observations
  ENABLE TRIGGER capture_observations_immutable;

ALTER TABLE capture_observations
  ALTER COLUMN locator_kind SET NOT NULL,
  ADD CONSTRAINT capture_observations_locator_shape CHECK (
    (locator_kind = 'native_id'
      AND locator_native_id IS NOT NULL
      AND length(btrim(locator_native_id)) > 0
      AND locator_byte_offset IS NULL
      AND locator_byte_length IS NULL)
    OR
    (locator_kind = 'byte_range'
      AND locator_native_id IS NULL
      AND locator_byte_offset IS NOT NULL
      AND locator_byte_offset >= 0
      AND locator_byte_length IS NOT NULL
      AND locator_byte_length > 0)
  ),
  ADD CONSTRAINT capture_observations_stream_typed_locator_unique
    UNIQUE NULLS NOT DISTINCT
      (stream_uuid, locator_kind, locator_native_id,
       locator_byte_offset, locator_byte_length),
  DROP COLUMN source_locator;
