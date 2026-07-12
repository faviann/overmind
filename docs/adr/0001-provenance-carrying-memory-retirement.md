# Record memory retirement as a provenance-carrying action

Retiring a memory changes what the system presents as current knowledge, so a timestamp-only status flip loses material audit information. Retirement therefore records a named operator and reason as an append-only `retirement` trace event, atomically with the `approved` → `retired` transition; it remains distinct from approval and rejection because it withdraws existing knowledge rather than adjudicating a proposal.
