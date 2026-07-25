-- A relationship's optional source-stream scope is immutable source evidence.
-- NULL means the source stated no stream scope; it is not the source event's
-- stream and must not be filled during retrieval.
ALTER TABLE captured_event_relationships
  ADD COLUMN target_source_stream_uuid UUID,
  DROP CONSTRAINT captured_event_relationships_source_trace_uuid_relationship_key,
  ADD CONSTRAINT captured_event_relationships_source_target_unique
    UNIQUE NULLS NOT DISTINCT
      (source_trace_uuid, relationship_type, target_source_stream_uuid,
       target_native_id, target_kind);
