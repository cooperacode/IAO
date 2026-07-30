<!-- generated-by: Flows.Refinement -->
<!-- refinement_run_id: 7ee5e9dc-bfbd-4d1b-8000-1a472c5e35a0 -->
<!-- published_at: 2026-07-28T02:35:42Z -->
<!-- status: approved -->
<!-- source_files: 202607211323-todo-app-brief.md,adr-0001-vertical-slice.md,c4-diagrama-componentes.md,todoapp-refinement-decisions.md -->

> **Notice:** this document was generated automatically by the refinement flow
> (`Flows.Refinement`) and approved by the flow's own deterministic review gate
> (the `review` command with a `READY` verdict). It does NOT represent explicit
> human approval — unless a human approval is recorded in the content below —
> because this MVP does not collect formal human approval before publication.
# Project Charter — TodoApp WebAPI

## 1. Context and problem/opportunity
TodoApp WebAPI is a self-contained, demonstrative greenfield project. The opportunity is to provide task management over HTTP with real persistence in a local Postgres, preserving data across API-only restarts, and offering reproducible automated verification. There is no prior implementation, JSON file, contract, or legacy data to migrate; the migration wording in the original ADR's context is obsolete.

## 2. Stakeholders and users
- **HTTP client/consumer:** the functional user who creates, queries, filters, completes, edits, and removes tasks.
- **Developers:** build and maintain the slices, persistence, and local infrastructure; availability not specified.
- **Testers/reviewers:** verify pure rules and endpoints against a real Postgres; availability not specified.
- **Requester:** the human source of the greenfield scope decision and the acceptance stakeholder.
- No sponsor, production team, or named owners were specified; this does not change the confirmed local, demonstrative character of the project.

## 3. Objectives
- **OBJ-001 — Functional management:** provide task creation, listing, filtering, completion, editing, and removal through the WebAPI, with clear HTTP errors.
- **OBJ-002 — Reliable local persistence:** store tasks in Postgres, create the schema on a fresh database, and preserve data across API-only restarts.
- **OBJ-003 — Reproducible verification:** bring up/wait for Postgres and run real, isolated unit and integration tests through a single idempotent flow.
- **OBJ-004 — Evolution through cohesive slices:** keep each endpoint in its own vertical slice and restrict the shared kernel to what is genuinely common.
- **OBJ-005 — Safe failures in the local environment:** validate inputs, externalize the connection, and avoid exposing internal details on failure.

## 4. Success metrics
- **MET-001 — Functional acceptance:** every endpoint and scenario in the brief has a passing integration test over real HTTP/Postgres.
- **MET-002 — Pure rules:** validation/normalization, states, and filtering have passing unit tests with no HTTP/Postgres involved.
- **MET-003 — Proven persistence:** a task keeps its id, title, and status after an API-only restart.
- **MET-004 — Idempotent startup:** two successive runs of `init.sh` wait for Postgres and trigger verification with no manual setup.
- **MET-005 — Aggregate result:** the single command returns success only when both test suites are fully green.
- **MET-006 — Sanitized error:** database unavailability does not reveal the connection, credentials, stack trace, or driver details.

## 5. Scope
### In scope
- ASP.NET Core Web API in .NET/C#.
- Endpoints for creating, listing/filtering, completing, editing the title, and removing tasks.
- A task with a Postgres-generated id, a title, and a pending/completed status; trimming, idempotent completion, and edits that preserve status.
- A single local Postgres via Docker Compose, automatic schema creation, a persistent volume, and connection configuration through an environment variable with a local default.
- Vertical Slice Architecture with no generic Controller/Service/Repository.
- Unit tests for the rules and integration tests per endpoint over real HTTP/Postgres, with isolated data.
- `docker-compose.yml`, an idempotent `init.sh`, and a single verification command.

### Out of scope
- Migration, reading, or compatibility with JSON or any legacy data.
- Multiple users, login, authentication, authorization, and synchronization.
- Frontend, due dates, reminders, priorities, and tags.
- Cloud, production, CI, managed Postgres, Kubernetes, and multiple services.
- A dedicated mandatory migration tool, backup/restore, and any guarantee after the volume is removed.
- Use with regulated data or external operational exposure; any such evolution requires a new refinement.

## 6. Assumptions, constraints, and dependencies
### Assumptions
- Titles will contain only non-sensitive demonstration data; a change in classification requires a new refinement.
- Usage will remain within the local development/test environment described in the brief.
- The requester or a designated person will provide final acceptance, with no need to invent names at this stage.
- Delegated choices will be documented and will use supported versions/acceptable licenses.

### Constraints
- .NET/C#, ASP.NET Core, and Postgres are mandatory.
- One slice per endpoint; a minimal shared kernel.
- Integration testing uses a real Postgres, with no mocks or in-memory repository.
- Compose contains only Postgres; the local flow is idempotent.
- No deadline, budget, capacity, SLO, or minimum platform was specified.

### Dependencies
- .NET SDK/runtime, Docker Compose, a Postgres image, and a .NET data library still to be selected.
- Local ports/resources and a persistent volume.
- Definition of the HTTP contract, schema, and isolation strategy during Analysis/Design.
- Future availability of people with the necessary skills; no allocation is assumed.

## 7. Preliminary feasibility
- **Technical:** favorable. The stack is mature, the scope is small, and the ADR provides structure; library/schema choices are delegated to design.
- **Operational:** favorable for local development/testing with Docker and .NET; production is out of scope.
- **Economic:** undetermined due to the lack of a budget/effort estimate, but the local infrastructure requires no managed service.
- **Schedule:** undetermined due to the lack of a deadline/capacity; no date is promised.
- **Security/compliance:** adequate for the assumed local, non-sensitive use, conditioned on sanitization/external configuration; other contexts require reassessment.

## 8. Expected resources and skills
Without assuming availability: C#/ASP.NET Core, HTTP APIs, SQL/Postgres, Docker Compose, idempotent shell scripting, unit/integration testing, data isolation, review of validation/configuration/errors, and acceptance authority from the requester or a designated person. Headcount, names, budget, and allocation were not provided.

## 9. Milestones by phase
Without dates:
1. Planning: Charter and greenfield scope consolidated.
2. Analysis: SRS, behavioral contract, criteria, and traceability defined.
3. Design: components, data, technical decisions, threats, and test strategy defined.
4. Readiness/Handoff: the set reviewed and sliced for manual handoff to development.
5. Future implementation: solution and metrics proven; outside this documentation flow.

## 10. Risks
- **RSK-001 — Inconsistent HTTP contract.** Likelihood: medium. Impact: medium. Mitigation: fix casing, status codes, errors, and ordering in the SRS/SDD. Owner: technical/product role to be assigned.
- **RSK-002 — Contaminated tests.** Likelihood: medium. Impact: high. Mitigation: a deterministic cleanup/isolation strategy and readiness checks. Owner: development/testing to be allocated.
- **RSK-003 — Shared kernel growth.** Likelihood: medium. Impact: medium. Mitigation: structural review per slice. Owner: technical leadership to be assigned.
- **RSK-004 — Configuration leakage.** Likelihood: low. Impact: high. Mitigation: environment-based configuration, sanitized responses, and negative tests. Owner: development to be allocated.
- **RSK-005 — Environment incompatibility.** Likelihood: medium. Impact: medium. Mitigation: document supported versions and validate on a clean environment. Owner: technical role to be assigned.
- **RSK-006 — Missing capacity limits.** Likelihood: low for demonstration use. Impact: medium. Mitigation: make no performance claims; reassess before expanding use. Owner: product/technical.
- **RSK-007 — Use beyond the local boundary.** Likelihood: low. Impact: high due to the lack of authentication. Mitigation: document the boundary and require a new refinement for exposure/multiple users. Owner: requester/future operator.
- **RSK-008 — Sensitive data in the title.** Likelihood: low under the demonstration assumption. Impact: high. Mitigation: do not use sensitive data, and reopen privacy/compliance review if the classification changes. Owner: user/requester.

## 11. Security, privacy, and compliance objectives
- Validate inputs before any mutation and use parameterized data operations.
- Externalize the connection; limit the default to the local Compose setup and never log credentials.
- Sanitize failure responses so they contain no connection details, stack traces, or internal messages.
- Document that there is no identity/authorization and that the product is not approved for external exposure.
- Treat titles as non-sensitive demonstration data; do not claim compliance beyond this scope.
- Reopen refinement if production, external networking, multiple users, or regulated data come into play.

## 12. Confirmed decisions
- Greenfield project, with no legacy data or JSON migration; the conflicting ADR context is superseded.
- .NET/C#, ASP.NET Core, Postgres, and local Docker Compose.
- Vertical Slice Architecture and a minimal shared kernel.
- API only, with no frontend, users, or authentication.
- Unit tests for pure rules and integration tests over real HTTP/Postgres.
- Persistence across API restarts with the volume retained.
- Local development/test use; production/external exposure out of scope.

## 13. Open questions
- Supported versions and the data/testing library.
- JSON casing, status representation, error schema, ordering, and 500 versus 503.
- Schema mechanism and test isolation.
- Title length/uniqueness limits, pagination, concurrency, and performance targets, none of which are required for the current demonstration use.
- Local platforms and named owners.

These questions are technical or execution choices within the confirmed boundary and should be decided/documented during Analysis/Design or before implementation. They do not reopen the greenfield scope and do not authorize external use.
