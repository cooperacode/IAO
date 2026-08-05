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
# Readiness Handoff — TodoApp WebAPI

## 1. Final recommendation and rationale
**READY.** The document set is complete, coherent, and traceable for the greenfield/local scope. The human decision resolved the one scope conflict; requirements have observable objectives and criteria; every RF/RNF/SEC is allocated; technical decisions have alternatives/trade-offs; threats and controls cover the security requirements. The remaining questions concern version pinning/operational assignment or future expansions outside the baseline.

## 2. Document and version inventory
Refinement run `7ee5e9dc-bfbd-4d1b-8000-1a472c5e35a0`, baseline version 1:
- `.harness/discovery.md` — consolidated context, four sources.
- `.harness/01-project-charter.md` — Project Charter, OBJ-001 to OBJ-005, MET-001 to MET-006, and RSK-001 to RSK-008.
- `.harness/02-software-requirements-specification.md` — SRS, RF-001 to RF-009, RN-001 to RN-010, RNF-001 to RNF-011, and SEC-001 to SEC-005.
- `.harness/03-software-design-document.md` — SDD, ADR-001 to ADR-010, and the full allocation.
- `specs/todoapp-refinement-decisions.md` — the human greenfield decision that reconciles the sources.

## 3. Confirmed decisions
- A greenfield product, with no legacy file/data/contract and no JSON migration.
- A .NET/C#/ASP.NET Core WebAPI, with local Postgres as a single Docker Compose service.
- Vertical slices per endpoint, Npgsql used directly, and a minimal shared kernel.
- An idempotent initial schema applied at API startup.
- Canonical contract: `id`, `titulo`, `status`; status values `pendente`/`concluida`; Problem Details; `503` for an unavailable database; listing ordered by id.
- xUnit at two levels; real HTTP/Postgres integration tests, run serially, with truncate/reset before each case.
- Connection via `TODO_DB_CONNECTION`; logs contain no payloads/titles/secrets.
- Local development/test use with no authentication; production, external exposure, and regulated data are out of scope.

## 4. Traceability matrix
| Objective | Related RF/RNF/SEC | Components and design decisions |
|---|---|---|
| OBJ-001 | RF-001 to RF-006; RNF-001; SEC-003, SEC-004 | Entry Point and the five slices; ADR-001, ADR-010 |
| OBJ-002 | RF-001, RF-002, RF-004 to RF-008; RNF-001, RNF-005, RNF-006, RNF-010; SEC-002, SEC-003 | Postgres, Schema Bootstrap, Shared Kernel; ADR-003 to ADR-006, ADR-009 |
| OBJ-003 | RF-007 to RF-009; RNF-001 to RNF-007, RNF-011; SEC-002 | Compose/init, Unit/Integration Tests; ADR-004, ADR-005, ADR-007 to ADR-009 |
| OBJ-004 | RNF-002, RNF-008, RNF-009 | Slices, Shared Kernel, and Unit Tests; ADR-001, ADR-003, ADR-006, ADR-007 |
| OBJ-005 | RF-008; SEC-001 to SEC-005 | Error Boundary, configuration, logs, and negative tests; ADR-003, ADR-009, ADR-010 |

The individual definitions in the SRS record the exact links, including requirements tied to more than one objective. The SDD's allocation table confirms a target for RF-001 through RF-009, RNF-001 through RNF-011, and SEC-001 through SEC-005.

## 5. Residual risks and pending items
- Exact SDK, Postgres image, and library versions must be pinned/documented in the first slice; this is an operational decision within the approved design.
- Concurrent bootstrap, Unicode/constraints, truncate/reset, host restart, and Npgsql redaction still need to be proven by the planned tests.
- Serial integration reduces parallelism, an accepted trade-off for determinism given the small scope.
- There is no SLA, pagination, backup, or strong concurrency; any future need requires a new refinement.
- The unauthenticated API cannot be promoted to external/production use.
- Titles are non-sensitive demonstration data; a change in classification requires reassessment.
- Named owners and reviewers will be assigned when each slice begins; the roles are defined below.

## 6. Conflicts found and resolution
- The original ADR mentioned a JSON migration, while the brief described a greenfield project. The human decision in `specs/todoapp-refinement-decisions.md` resolved the conflict: greenfield prevails and the old wording is obsolete context; the vertical-slices decision remains valid.
- The diagram presents ListTasks as a single component for both listing and filtering; the SRS confirms both belong to the same slice, with no orphaned endpoint.
- The source allowed either 500 or 503 for an unavailable database; the SRS/SDD standardized on 503 with Problem Details.
- The library, bootstrap approach, and isolation strategy were delegated choices; the SDD chose Npgsql directly, bootstrap at startup, and serial integration with truncate/reset, recording the alternatives and trade-offs.

## 7. Decisions pending human approval
No material decision is pending for the current baseline. The greenfield decision was provided by the requester. Naming owners/reviewers and pinning versions are normal actions at the start of development. Any expansion into legacy data, production, external networking, multiple users, authentication, or regulated data depends on new human approval and a new refinement.

## 8. Explicit boundary for development
Development may start the .NET solution, local Postgres infrastructure, schema bootstrap, minimal shared kernel, and the five defined slices, with the specified contract/tests. It may choose supported versions and pin them without changing the architectural decisions. It may not add JSON migration, a frontend, identity, cloud, CI/production, multiple services, regulated data, pagination/SLA, or generic layers without a new refinement.

## 9. Slicing into vertical increments
Each slice below is demonstrable, verifiable, and sized at up to half a sprint for this small scope.

### Slice: postgres-infrastructure
- Actor and expected outcome: A developer can start a healthy Postgres, bring up the API with the schema ready, and run verification with one command.
- In scope: Solution/projects, pinned versions, Compose with a single Postgres/volume/healthcheck, NpgsqlDataSource, TODO_DB_CONNECTION, Schema Bootstrap, init.sh, and an aggregate command.
- Out of scope: Business endpoints, CI, production, cloud, and a dedicated migration tool.
- Requirements covered: RF-007, RF-008, RF-009, RNF-004, RNF-005, RNF-007, RNF-011, SEC-002, SEC-004
- Dependencies: none
- API/event contract: An operational contract; the database is ready before tests run, the schema is idempotent, and the exit code is zero only when both suites are green.
- Happy path: On a clean environment, init.sh brings up/waits for Postgres, the API initializes the schema, and GET /tasks can respond with an empty 200 once the slice exists.
- Main failure scenario: Postgres does not become healthy or the connection is invalid; verification fails without exposing credentials.
- Automated test: Verification of two consecutive startups, a fresh database, a connection override, and a failing aggregate result.
- Expected demonstration: One command prepares the infrastructure and runs the test projects with no manual intervention.
- Owner: Platform/API developer to be assigned.
- Reviewer: Technical reviewer to be assigned.

### Slice: add-task
- Actor and expected outcome: The consumer creates a valid task and receives the persisted, pending record.
- In scope: POST /tasks, validation/trimming, id/status, an Npgsql INSERT, Location, Problem Details, and unit/integration tests.
- Out of scope: Listing, completion, editing, removal, and authentication.
- Requirements covered: RF-001, RNF-001, RNF-002, RNF-008, RNF-009, SEC-003
- Dependencies: postgres-infrastructure
- API/event contract: JSON with titulo; 201 with id, titulo, pending status, and Location; 400 Problem Details for a missing/empty title.
- Happy path: Creating "Comprar leite" returns 201 and persists the normalized title as pending.
- Main failure scenario: A missing, empty, or whitespace-only title returns 400 and inserts nothing.
- Automated test: Unit tests for validation/trimming and HTTP integration tests against Postgres for all creation scenarios.
- Expected demonstration: A POST followed by a database inspection shows exactly one equivalent task.
- Owner: AddTask slice developer to be assigned.
- Reviewer: API/test reviewer to be assigned.

### Slice: list-filter-tasks
- Actor and expected outcome: The consumer queries all tasks or only one status, in deterministic order.
- In scope: GET /tasks, the status query, an Npgsql SELECT, ordering by id, an empty array, sanitized 400/503 responses, and tests.
- Out of scope: Task mutation, pagination, and SLA.
- Requirements covered: RF-002, RF-003, RNF-001, RNF-002, RNF-008, SEC-001, SEC-003
- Dependencies: add-task
- API/event contract: 200 with an array of id/titulo/status; pendente/concluida filters; 400 for an invalid filter; 503 for an unavailable database.
- Happy path: Two tasks are listed by id, and each filter returns only the requested state.
- Main failure scenario: a status of "urgent" returns 400 Problem Details with no tasks; unavailability returns a sanitized 503.
- Automated test: Unit tests for filtering and integration tests for a full/empty list, both filters, an invalid value, and an unavailable database.
- Expected demonstration: Requests with no filter and with each status show correct, ordered sets.
- Owner: ListTasks slice developer to be assigned.
- Reviewer: API/test reviewer to be assigned.

### Slice: complete-task
- Actor and expected outcome: The consumer marks an existing task as completed, idempotently.
- In scope: PATCH /tasks/{id}/complete, a parameterized UPDATE, returning the task, 404, and tests.
- Out of scope: Title editing, removal, and reopening back to pending.
- Requirements covered: RF-004, RNF-001, RNF-002, RNF-008, SEC-003
- Dependencies: add-task
- API/event contract: 200 with status concluida for an existing task/repeated call; 404 Problem Details for a nonexistent id.
- Happy path: A pending task becomes concluida in both the body and the database.
- Main failure scenario: A nonexistent id returns 404 with no change to other records.
- Automated test: Unit tests for idempotency and integration tests for pending, already-completed, and missing tasks.
- Expected demonstration: Two successive calls keep the same task completed and both return 200.
- Owner: CompleteTask slice developer to be assigned.
- Reviewer: API/test reviewer to be assigned.

### Slice: edit-task
- Actor and expected outcome: The consumer corrects the title without changing the task's state.
- In scope: PUT /tasks/{id}, validation/trimming, a parameterized UPDATE, status preservation, 400/404, and tests.
- Out of scope: Status changes, version history, and strong concurrency.
- Requirements covered: RF-005, RNF-001, RNF-002, RNF-008, SEC-003
- Dependencies: add-task
- API/event contract: JSON with titulo; 200 with the updated task; 400 for an invalid title; 404 for a nonexistent id.
- Happy path: Changing the title to "Comprar leite integral" persists the new title and keeps the status unchanged.
- Main failure scenario: An empty title returns 400 and keeps the previous value.
- Automated test: Unit tests for validation/preservation and integration tests for a valid, empty, missing, and completed task.
- Expected demonstration: A PUT followed by a query/database check shows only the title changed.
- Owner: EditTask slice developer to be assigned.
- Reviewer: API/test reviewer to be assigned.

### Slice: remove-task
- Actor and expected outcome: The consumer permanently removes an existing task.
- In scope: DELETE /tasks/{id}, a parameterized DELETE, 204/404, and tests.
- Out of scope: Trash/recycle bin, restore, soft delete, and audit.
- Requirements covered: RF-006, RNF-001, RNF-002, RNF-008, SEC-003
- Dependencies: add-task
- API/event contract: 204 with no body for an existing task; 404 Problem Details for a missing one.
- Happy path: A pending or completed task is removed and no longer appears.
- Main failure scenario: A nonexistent id returns 404 with no change to the database.
- Automated test: Unit tests for the affected-rows decision and integration tests for existing, completed, and missing tasks.
- Expected demonstration: DELETE returns 204 and a subsequent query no longer contains the id.
- Owner: RemoveTask slice developer to be assigned.
- Reviewer: API/test reviewer to be assigned.

### Slice: persistence-resilience
- Actor and expected outcome: A developer proves persistence, isolation, and safe failure handling for the local stack.
- In scope: Host restart while keeping Postgres, truncate/reset per test case, serial execution, log/response redaction, and aggregate scenarios.
- Out of scope: Backup, high availability, performance, production, and suite parallelism.
- Requirements covered: RF-002, RF-007, RNF-003, RNF-006, RNF-010, SEC-001, SEC-005
- Dependencies: list-filter-tasks
- API/event contract: The same HTTP contracts; a task preserves id/titulo/status after a restart, and failures still return sanitized Problem Details.
- Happy path: Creating a task, recreating the API host, and listing returns exactly the same task.
- Main failure scenario: Test state leaks between cases, or an Npgsql detail appears in a response/log; the suite must fail.
- Automated test: Individual, full, and repeated runs; an API restart; inspection of the response/headers/logs for titles/secrets.
- Expected demonstration: Persistence is visible after a restart, and two suite runs remain green and isolated.
- Owner: Integration/quality developer to be assigned.
- Reviewer: Technical and security reviewer to be assigned.

## 10. Suggested path for Flows.Development
After automatic publication, use together:
- `specs/todoapp-webapi-01-project-charter.md`
- `specs/todoapp-webapi-02-software-requirements-specification.md`
- `specs/todoapp-webapi-03-software-design-document.md`
- `specs/todoapp-webapi-04-readiness-handoff.md`

Handoff to Flows.Development remains manual, per the refinement protocol.
