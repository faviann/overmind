-- Browser-mediated, outbound-only Codex capture enrollment. Requests are
-- ephemeral mutable coordination state; audit rows are append-only.
CREATE TABLE capture_pairing_requests (
  request_uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_code TEXT NOT NULL UNIQUE,
  polling_token_hash TEXT NOT NULL UNIQUE,
  machine_name TEXT NOT NULL,
  codex_installation_id TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'pending'
    CHECK (status IN ('pending', 'approved', 'cancelled', 'delivered')),
  expires_at TIMESTAMPTZ NOT NULL,
  binding_uuid UUID REFERENCES capture_source_bindings(binding_uuid),
  delivery_credential TEXT,
  approved_by TEXT,
  approved_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CHECK ((status = 'approved' AND binding_uuid IS NOT NULL AND delivery_credential IS NOT NULL)
      OR (status = 'delivered' AND binding_uuid IS NOT NULL AND delivery_credential IS NULL)
      OR (status IN ('pending', 'cancelled') AND binding_uuid IS NULL AND delivery_credential IS NULL))
);

-- A browser pairing may be requested more than once, but approval can create
-- only one durable binding for the installation identity detected by the
-- runtime. Existing operator-provisioned bindings remain compatible.
ALTER TABLE capture_source_bindings
  ADD COLUMN codex_installation_id TEXT;
CREATE UNIQUE INDEX capture_source_bindings_codex_installation
  ON capture_source_bindings(codex_installation_id)
  WHERE codex_installation_id IS NOT NULL;

CREATE TABLE capture_pairing_audit (
  audit_uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  request_uuid UUID NOT NULL REFERENCES capture_pairing_requests(request_uuid),
  action TEXT NOT NULL CHECK (action IN ('created', 'approved', 'cancelled', 'delivered')),
  operator_subject TEXT,
  occurred_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TRIGGER capture_pairing_audit_immutable
BEFORE UPDATE OR DELETE ON capture_pairing_audit
FOR EACH ROW EXECUTE FUNCTION forbid_mutation();

GRANT SELECT, INSERT, UPDATE ON capture_pairing_requests TO memsrv;
GRANT SELECT, INSERT ON capture_pairing_audit TO memsrv;
