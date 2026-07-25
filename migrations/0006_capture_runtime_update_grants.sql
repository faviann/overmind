-- Runtime capture authority is append plus checkpoint advancement. Binding
-- identity/routing and stream identity/routing remain provisioning-owned.
REVOKE UPDATE ON capture_source_bindings, capture_source_streams FROM memsrv;
GRANT UPDATE (checkpoint_position, updated_at)
  ON capture_source_streams TO memsrv;
