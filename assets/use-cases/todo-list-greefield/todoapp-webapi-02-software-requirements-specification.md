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
# Software Requirements Specification — TodoApp WebAPI

## 1. Product vision and system boundaries
A local HTTP WebAPI for a single task list. The client requests creation, listing/filtering, completion, editing, and removal; the API validates and persists to Postgres. Included: the API, the local database, startup, and verification. Excluded: legacy/JSON data, frontend, identity, cloud, production, multiple services, regulated data, and external exposure.

## 2. Glossary and actors
- **Task:** a record with `id`, `titulo`, and `status`.
- **Pending/completed:** semantic states serialized as `pendente` and `concluida`.
- **HTTP client:** an unauthenticated local consumer.
- **API restart:** restarting only the WebAPI while keeping Postgres/the volume running.
- **Fresh database:** a Postgres instance reachable with no application schema yet.
- **Real integration test:** an HTTP call against a real Postgres, with no mocks.
- **Actors:** the consumer; developers/testers; Postgres; Docker Compose; the requester/reviewer.

## 3. Use case, journeys, and main flows
### Create and query
Submit `titulo`; validate/normalize; persist as pending; return the task. Invalid input ends the request with no persistence. Listing returns all tasks ordered by ascending id, or an empty collection.

### Filter and update
Filter by `pendente`/`concluida`; complete idempotently; edit the title while preserving status; remove. An invalid filter/id/title produces an error with no unintended mutation.

### Initialize and verify
Bring up/wait for Postgres, create the schema automatically, and run unit/integration tests through a single command. Restarting only the API preserves tasks.

### Dependency unavailable
An operation that depends on the database returns `503 Service Unavailable` as Problem Details, with no internal details.

## 4. Functional requirements
- **RF-001 — Create task.** `POST /tasks` accepts JSON with `titulo`, persists the task as pending, and returns `201` with `id`, `status`, `titulo`, and a `Location` header. Acceptance: an empty database gains exactly one equivalent record; an invalid title creates nothing. Traces to: OBJ-001, OBJ-002.
- **RF-002 — List tasks.** `GET /tasks` returns `200` with an array of tasks ordered by ascending `id`. Acceptance: existing tasks appear with all three fields; an empty database returns an empty array. Traces to: OBJ-001, OBJ-002.
- **RF-003 — Filter tasks.** `GET /tasks?status=pendente|concluida` returns only the requested state; no filter returns all tasks. Acceptance: both filters and the no-filter case produce the correct sets; an invalid value is rejected. Traces to: OBJ-001.
- **RF-004 — Complete task.** `PATCH /tasks/{id}/complete` persists `concluida` and returns `200` with the task. Acceptance: body/database change; repeating the call still returns `200`; a missing task causes no change. Traces to: OBJ-001, OBJ-002.
- **RF-005 — Edit title.** `PUT /tasks/{id}` accepts `titulo`, normalizes/persists it, and returns `200` with the task. Acceptance: the title changes and the status is unchanged; an invalid title/id causes no change. Traces to: OBJ-001, OBJ-002.
- **RF-006 — Remove task.** `DELETE /tasks/{id}` deletes an existing task and returns `204` with no body. Acceptance: the record disappears; a missing task causes no change. Traces to: OBJ-001, OBJ-002.
- **RF-007 — Initialize a fresh database.** Automatically create the required table before first use. Acceptance: a Postgres instance with no table starts responding to listing with an empty `200` and no manual setup. Traces to: OBJ-002, OBJ-003.
- **RF-008 — Configure the connection.** Read `TODO_DB_CONNECTION`, using a default only for the local Compose setup when it is absent. Acceptance: overriding it changes the target and no external credential is hardcoded. Traces to: OBJ-002, OBJ-003, OBJ-005.
- **RF-009 — Initialize and verify.** Provide a Postgres instance in Compose, an idempotent `init.sh`, and a single command after readiness. Acceptance: two successive runs with no intervention execute both test suites. Traces to: OBJ-003.

## 5. Business rules
- **RN-001 — Required/normalized title.** A missing, empty, or whitespace-only title is invalid; trimming is applied. Acceptance: invalid input returns `400`; leading/trailing whitespace is never persisted. Traces to: OBJ-001, OBJ-005.
- **RN-002 — Initial state.** A newly created task starts as `pendente`. Acceptance: both the response and the database confirm this. Traces to: OBJ-001.
- **RN-003 — Stable identity.** Postgres generates an increasing positive integer. Acceptance: each creation generates a distinct id, and queries preserve it. Traces to: OBJ-001, OBJ-002.
- **RN-004 — Status domain.** Only `pendente` and `concluida` are valid. Acceptance: no operation produces a third value. Traces to: OBJ-001.
- **RN-005 — Idempotent completion.** Completing an already-completed task keeps its state and record unchanged. Acceptance: repeated calls return `200` with no duplication. Traces to: OBJ-001.
- **RN-006 — Editing preserves status.** Changing the title does not change the status. Acceptance: both pending and completed tasks keep their state. Traces to: OBJ-001, OBJ-002.
- **RN-007 — Missing task causes no change.** Completing/editing/removing a nonexistent id returns `404`. Acceptance: the database is identical before and after. Traces to: OBJ-001, OBJ-005.
- **RN-008 — Invalid filter.** A status outside the domain returns `400` and includes no tasks in the body. Acceptance: Problem Details with no data array. Traces to: OBJ-001, OBJ-005.
- **RN-009 — Atomic rejection.** An invalid title returns `400` before any mutation. Acceptance: creation does not increase the total count; editing keeps the previous value. Traces to: OBJ-001, OBJ-005.
- **RN-010 — Persistence across restart.** Restarting only the API does not modify tasks. Acceptance: id/title/status remain unchanged. Traces to: OBJ-002.

## 6. Non-functional requirements
- **RNF-001 — Full integration coverage.** Measurable target: 100% of endpoints RF-001 through RF-006 and the brief's scenarios have a passing real HTTP/Postgres test. Acceptance: a scenario-to-test inventory with no gaps. Traces to: OBJ-001, OBJ-002, OBJ-003.
- **RNF-002 — Full unit coverage.** Measurable target: every applicable pure rule RN-001 through RN-009 has a passing unit test with no HTTP/Postgres involved. Acceptance: a rule-to-test inventory with no gaps. Traces to: OBJ-003, OBJ-004.
- **RNF-003 — Isolation.** Measurable target: integration tests pass individually, as a suite, and across two consecutive runs, with no dependency on order or leftover state. Acceptance: all three modes pass. Traces to: OBJ-003.
- **RNF-004 — Idempotent startup.** Measurable target: two successive runs of `init.sh` require no cleanup or editing. Acceptance: both runs verify successfully. Traces to: OBJ-003.
- **RNF-005 — Readiness.** Measurable target: verification starts only after a positive healthcheck; a fresh database lists as `200`. Acceptance: an observable sequence. Traces to: OBJ-002, OBJ-003.
- **RNF-006 — Observable persistence.** Measurable target: after an API-only restart, a query returns the same three attributes. Acceptance: full equality. Traces to: OBJ-002.
- **RNF-007 — Single result.** Measurable target: one invocation runs both suites; success only with 100% passing. Acceptance: an induced failure in either suite makes the overall result unsuccessful. Traces to: OBJ-003.
- **RNF-008 — Cohesion per endpoint.** Measurable target: all six endpoints keep their specific rules/data within their own slice; zero generic Controller/Service/Repository. Acceptance: structural review confirms this. Traces to: OBJ-004.
- **RNF-009 — Minimal shared kernel.** Measurable target: the shared code contains only the entity/status, the connection/pool, and common errors; zero endpoint-specific rules/queries. Acceptance: review confirms this. Traces to: OBJ-004.
- **RNF-010 — Retention in the volume.** Measurable target: data survives an API restart with the same volume; removing the volume is not guaranteed to preserve data. Acceptance: the restart scenario passes. Traces to: OBJ-002.
- **RNF-011 — Reproducible environment.** Measurable target: supported versions are documented and a clean local environment completes `init.sh`/verification. Acceptance: a passing run on the documented set. Traces to: OBJ-003.

There is no latency, throughput, scale, or pagination requirement for this local demonstration; no corresponding claim is part of the acceptance criteria.

## 7. Security and privacy requirements
- **SEC-001 — Sanitized error.** A failure response contains no connection details, credentials, stack trace, internal host, or driver message. Acceptance: the body/headers contain none of these five categories. Traces to: OBJ-005.
- **SEC-002 — External configuration.** No hardcoded external credential; the connection is overridable via the environment, with a local-only default. Acceptance: inspection/override confirm this. Traces to: OBJ-002, OBJ-005.
- **SEC-003 — Validation before mutation.** Invalid input is rejected before any change command runs. Acceptance: Postgres remains unchanged. Traces to: OBJ-001, OBJ-005.
- **SEC-004 — No identity/exposure.** No user, session, token, or authorization is created; local-only use is documented. Acceptance: the inventory contains no identity mechanisms, and the documentation excludes external exposure. Traces to: OBJ-001, OBJ-005.
- **SEC-005 — Demonstration data.** Titles are not logged by default, and no compliance claim is made for regulated data. Acceptance: scenario logs contain no titles; the limitation is documented. Traces to: OBJ-005.

## 8. Data, retention, classification, and integrations
Task: a positive `id` generated by Postgres, a `titulo` that is non-empty text after trimming, and a `status` within the domain. No additional uniqueness or maximum length has been confirmed; storage must support the examples given. Retention lasts until `DELETE` or volume removal; there is no history, backup, audit, or regulatory retention. The title is assumed to be non-sensitive demonstration data; changing that assumption requires a new refinement.

Integrations: HTTP client↔API; API↔Postgres; Compose↔Postgres; verifier↔API/database/both test suites. No external/cloud/legacy integration.

## 9. External interfaces and dependencies
- Success and task responses use `application/json`; errors use `application/problem+json` with `type`, `title`, `status`, `detail`, and `instance`.
- Task fields: `id`, `titulo`, `status`; status values: `pendente`, `concluida`.
- `GET /tasks` orders results by ascending id.
- An unavailable database returns `503` with sanitized Problem Details.
- Mandatory dependencies: .NET/C#, ASP.NET Core, Postgres, and Docker Compose; the library/versions and schema mechanism are Design decisions.

## 10. Error states and alternate flows
Invalid title/filter: `400`; missing id: `404`; database unavailable: `503`; all as Problem Details, with no unintended mutation. An empty list: `200` with an empty array. Repeated completion: `200`. A fresh database initializes itself. An API-only restart preserves data.

## 11. Acceptance criteria per requirement
Each RF/RN/RNF/SEC definition includes a normative, verifiable `Acceptance` clause. There are no separate redefinitions of the IDs.

## 12. Assumptions and open questions
Assumptions: local/demonstrative use, non-sensitive titles, and acceptance by the requester/designee. Technical questions that do not block detailed implementation: versions/library, schema mechanism, exact isolation approach, concurrency, and local platform. There are no performance/pagination/additional limit requirements; any future need requires a new refinement.

## 13. Traceability
Each definition points, via `Traces to`, to one of the existing OBJ-001 through OBJ-005. The SDD must allocate every RF/RNF/SEC to components/decisions and preserve the greenfield/local boundary.
