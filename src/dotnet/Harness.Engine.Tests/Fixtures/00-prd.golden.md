# Product Requirements Document

## Vision
TodoApp WebAPI gives a single user reliable, self-contained task management over HTTP, with real Postgres persistence and reproducible automated verification.

## Goals
- **OBJ-001:** Functional management: create, list, filter, complete, edit and remove tasks through the WebAPI.
- **OBJ-002:** Reliable local persistence: tasks survive an API-only restart.

## Success Metrics
- **MET-001** (OBJ-001): endpoints and scenarios in the brief with a passing integration test → 100% covered
- **MET-002** (OBJ-002): a task's id/title/status after an API-only restart → unchanged

## Non-Goals
- Migration or compatibility with legacy/JSON data.
- Multiple users, login, authentication or authorization.

## Scope
- ASP.NET Core Web API in .NET/C\#.
- A single local Postgres via Docker Compose.

## Risks
- **RSK-001** (medium): Inconsistent HTTP contract across endpoints. — mitigation: Fix casing, status codes and ordering in the SRS/SDD.

## Decisions
- **DEC-001:** Vertical Slice Architecture with a minimal shared kernel. — rationale: Keeps each endpoint cohesive and independently testable.

## Open Questions
- **Q-1** [non-blocking]: Which pagination \*strategy\* \[if any\] applies to \`GET /tasks\`?
