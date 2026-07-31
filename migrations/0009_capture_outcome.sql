-- Immutable, server-authored content-free capture outcome provenance. Existing
-- observations predate outcome accounting and therefore remain healthy and
-- complete.
ALTER TABLE capture_observations
  ADD COLUMN capture_outcome JSONB NOT NULL DEFAULT
    '{"contractVersion":1,"captureHealth":"healthy","captureFidelity":"complete","counters":[]}'::jsonb;

ALTER TABLE capture_observations
  ADD CONSTRAINT capture_observations_outcome_object
    CHECK (jsonb_typeof(capture_outcome) = 'object');
