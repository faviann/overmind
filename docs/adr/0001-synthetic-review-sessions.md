# Use synthetic review sessions for V0 review events

V0 review events use `session_id: review:<proposal_id>` and `agent_id: <reviewer identity>` instead of reusing the source event session. This avoids misattributing a human or policy approval/rejection decision to the agent that produced the source event, while keeping the trace ledger schema simple until richer review sessions or authenticated actors exist.
