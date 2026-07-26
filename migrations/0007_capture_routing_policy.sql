-- Binding-scoped capture routing policy is append-only operator configuration.
-- The newest policy row is effective prospectively; established stream routes
-- remain fixed in capture_source_streams.
CREATE TABLE capture_route_policies (
  policy_uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  policy_version BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
  binding_uuid UUID NOT NULL REFERENCES capture_source_bindings(binding_uuid),
  allowed_repository_patterns TEXT[] NOT NULL,
  remote_overrides JSONB NOT NULL,
  directory_routes JSONB NOT NULL,
  special_namespaces JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX capture_route_policies_binding_version
  ON capture_route_policies(binding_uuid, policy_version DESC);

CREATE TRIGGER capture_route_policies_immutable
BEFORE UPDATE OR DELETE ON capture_route_policies
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

-- Legacy enrollment route columns are preserved for compatibility. Capture
-- routing authority ignores them in favor of policy and established streams.

ALTER TABLE capture_observations
  ADD COLUMN route_evidence JSONB;

ALTER TABLE capture_observations
  DISABLE TRIGGER capture_observations_immutable;

UPDATE capture_observations
SET route_evidence = 'null'::jsonb;

ALTER TABLE capture_observations
  ENABLE TRIGGER capture_observations_immutable;

ALTER TABLE capture_observations
  ALTER COLUMN route_evidence SET NOT NULL;

ALTER TABLE capture_source_streams
  DROP CONSTRAINT capture_source_streams_route_basis_check,
  ADD CONSTRAINT capture_source_streams_route_basis_check
    CHECK (route_basis IN ('configured_binding', 'override', 'origin', 'directory_mapping', 'fallback'));

ALTER TABLE capture_observations
  DROP CONSTRAINT capture_observations_route_basis_check,
  ADD CONSTRAINT capture_observations_route_basis_check
    CHECK (route_basis IN ('configured_binding', 'override', 'origin', 'directory_mapping', 'fallback'));

GRANT SELECT, INSERT ON capture_route_policies TO memsrv;
GRANT USAGE, SELECT ON SEQUENCE capture_route_policies_policy_version_seq TO memsrv;
