# Agent Operating Instructions

**Project**: Cortex — self-hosted personal AI assistant for task capture, triage, and execution  
**Full brief**: README.md  
**v1 PRD**: https://github.com/faviann/cortex/issues/1

## Stack

| Component | Role | Deployed on |
|-----------|------|-------------|
| n8n (self-hosted) | Integration, ingestion, event handling, external API calls | `workstation` LXC |
| Hermes (self-hosted) | On-demand reasoning agent, skills execution | `workstation` LXC |
| Postgres | Pre-validation queue (managed by n8n) | `workstation` LXC |
| OB1 | Personal knowledge graph — **v3, not yet built** | TBD |

LLM workloads route through external APIs (OpenAI, OpenRouter, Anthropic). No local model required.

## Companion Repos

- `faviann/cortex` — this repo. Architecture, PRDs, stack definitions, Hermes skills
- `faviann/cortex-tasks` — validated task list as GitHub Issues. Labels: `source:discord`, `source:email`, `importance:low/medium/high`, `status:parked`

## Key Constraints

- Everything self-hosted. No cloud dependencies beyond LLM API calls
- n8n is the only always-on process. Hermes activates on user request only
- Never expose gateway ports publicly without authentication
- Hermes connects to n8n via n8n's MCP server — no custom bridge code
- Do not build OB1 integration until v3 is explicitly scoped

## Versioning

Current target: **v1 — Capture & Execute**. See README.md for full roadmap. Do not implement v2+ features in v1 work.
