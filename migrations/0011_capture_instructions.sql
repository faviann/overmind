-- Server-owned, binding-scoped capture policy operations. The operation
-- vocabulary is closed here as well as in application code. Acknowledgement
-- is the only mutable instruction field and is deliberately replay-safe.
CREATE TABLE capture_instructions (
  instruction_uuid UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  binding_uuid UUID NOT NULL REFERENCES capture_source_bindings(binding_uuid),
  operation TEXT NOT NULL CHECK (operation IN ('scan', 'retry', 'pause', 'resume')),
  created_by TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  acknowledged_at TIMESTAMPTZ
);

CREATE INDEX capture_instructions_pending
  ON capture_instructions(binding_uuid, created_at, instruction_uuid)
  WHERE acknowledged_at IS NULL;

GRANT SELECT, INSERT ON capture_instructions TO memsrv;
GRANT UPDATE (acknowledged_at) ON capture_instructions TO memsrv;

