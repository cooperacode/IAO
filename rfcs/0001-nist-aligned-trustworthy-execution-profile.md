# RFC-IAO-0001: NIST-Aligned Trustworthy Execution Profile for Inverted Agentic Orchestration

| Field | Value |
|---|---|
| Document ID | RFC-IAO-0001 |
| Intended status | Project Standards Track — Proposed Standard |
| Version | 1.0.0-draft.1 |
| Author | Yan Justino |
| Contact | contact@yanjustino.com |
| Organization | cooperacode.club |
| Created | 2026-07-26 |
| Review cycle | At least annually and after any material security, model, protocol, or execution-boundary change |
| Repository baseline | `7d4fcb541ad36385d2ff627ffdcbafde4f95659f` |
| NIST alignment baseline | NIST AI 100-1; NIST AI 600-1; NIST SP 800-218; NIST SP 800-53 Rev. 5, Release 5.2.0; NIST SP 800-53A Rev. 5; NIST SP 800-18 Rev. 2 |
| Supersedes | None |

## Abstract

This document specifies a security, privacy, governance, and assurance profile for Inverted Agentic Orchestration (IAO), a pattern in which a deterministic software harness directs a tool-using generative-AI agent through a persistent state machine.

The current IAO implementation successfully moves workflow sequencing, bounded iteration, response validation, persistence, and trace generation from model memory into code. It provides protocol-compatible .NET, Python, and Rust engines and a development workflow that plans, implements, verifies, and commits features incrementally. That baseline improves reproducibility and resumability, but it does not by itself establish a trustworthy execution boundary. The supervised agent and target repository can currently influence policy, prompts, verification, state, evidence, filesystem scope, and Git effects.

This RFC defines the controls required to evolve IAO from deterministic orchestration to deterministic, evidence-producing, risk-governed execution. It defines trust boundaries, a versioned protocol, run identity, policy isolation, least-privilege capabilities, independent verification, tamper-evident evidence, privacy controls, supply-chain requirements, cross-engine conformance, assessment procedures, and migration rules.

This document is aligned with final NIST publications applicable to an AI-enabled software-development system. It is not a NIST publication, does not assert NIST certification, and does not by itself grant an authorization to operate.

## Status of This Memo

This memo is a project-level Proposed Standard intended for public technical review and adoption by IAO maintainers, implementers, security assessors, and downstream integrators.

NIST does not define an RFC publication process. Accordingly, this document combines:

1. the specification conventions of an Internet-Draft or RFC, including BCP 14 requirement language;
2. the system-description and control-implementation concerns of NIST SP 800-18 Rev. 2;
3. AI risk governance from NIST AI RMF 1.0 and the Generative AI Profile;
4. secure software-development practices from NIST SSDF 1.1; and
5. a tailored control crosswalk to NIST SP 800-53 Rev. 5.

NIST AI RMF 1.0 is under revision as of this document's publication date, and NIST SP 800-218 Rev. 1 / SSDF 1.2 is a public draft. This RFC therefore uses the latest final publications as its normative NIST baseline. A future revision of this RFC MUST assess and document the effect of superseding final NIST publications.

## 1. Conventions and Terminology

### 1.1 Requirement language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHALL**, **SHALL NOT**, **SHOULD**, **SHOULD NOT**, **RECOMMENDED**, **NOT RECOMMENDED**, **MAY**, and **OPTIONAL** in this document are to be interpreted as described in BCP 14, RFC 2119 and RFC 8174, when and only when they appear in all capitals.

### 1.2 Defined terms

**Agent**  
A generative-AI system, or an adapter around one, that interprets harness instructions and requests tool actions.

**Approval authority**  
A human or independently controlled service authorized to approve defined classes of side effect.

**Capability broker**  
The enforcement component that mediates filesystem, process, network, Git, credential, and external-service operations requested by an agent.

**Control plane**  
The trusted harness components, policy, state, keys, leases, and decision logic that govern a run.

**Driver**  
The IDE or runtime integration through which an agent participates in the protocol, such as Codex, Claude Code, GitHub Copilot, or Devin.

**Evidence**  
Information produced or collected to substantiate a claim about a run, control, build, test, approval, or release.

**Harness**  
The deterministic state machine, protocol dispatcher, validators, stores, guardrails, and evidence producers that direct an agent.

**IAO**  
Inverted Agentic Orchestration.

**Policy authority**  
The source authorized to define run constraints. It MUST be outside the write authority granted to the supervised agent.

**Run**  
One identified execution of a flow from initialization or resumption to a terminal state.

**Target**  
The repository or directory that the agent is permitted to inspect or modify.

**TEVV**  
Testing, evaluation, verification, and validation.

**Trusted verifier**  
A verifier whose code, configuration, environment, and evidence are protected from the actor whose work is being verified.

**Workspace content**  
Files within the target or exchange area. Unless explicitly promoted by policy, workspace content is untrusted input.

## 2. Scope

This RFC applies to:

- the reusable harness engine;
- domain flows built on the engine;
- driver adapters;
- prompt and skill assembly;
- local state and evidence;
- subprocess, filesystem, Git, and network effects;
- packaging and distribution;
- session usage and cost reporting;
- all protocol-compatible engine implementations; and
- organizations deploying IAO as part of a software-development lifecycle.

This RFC addresses both the IAO software product and the AI-enabled system formed when that product is combined with an agent, driver, target repository, toolchain, and human operators.

### 2.1 In-scope deployment modes

1. Local developer workstation.
2. CI or ephemeral build worker.
3. Managed enterprise development environment.
4. Air-gapped or restricted-network environment.
5. Downstream packaged distribution using any supported engine and driver adapter.

### 2.2 Non-goals

This RFC does not:

- standardize a model-provider API;
- guarantee correctness of arbitrary generated code;
- replace organization-level risk assessment, legal review, or authorization;
- defend against an administrator or operating-system kernel that is already compromised;
- require a specific container, virtual machine, or sandbox product;
- require an organization to train or fine-tune a foundation model;
- define sector-specific safety controls; or
- claim that a conforming implementation is certified by NIST.

## 3. System Purpose and Authorization Boundary

IAO exists to make long-running agent workflows reproducible, bounded, resumable, testable, and auditable by locating workflow authority in deterministic code rather than conversational memory.

The minimum authorization boundary for a controlled IAO deployment includes:

- the harness executable and libraries;
- the active flow;
- policy and configuration;
- state, leases, and evidence;
- the capability broker;
- trusted verification infrastructure;
- driver adapter;
- model/provider connection;
- target repository;
- package and dependency sources;
- report generators; and
- operator approval and incident-handling processes.

The target repository is inside the operational boundary but outside the trust boundary. The supervised agent is also outside the trust boundary. Output from the agent, documents read from the target, target-owned scripts, Git metadata, dependency manifests, model responses, and model-provider metadata MUST be treated as untrusted until validated or independently verified.

## 4. Repository-Derived Baseline

### 4.1 Implemented architecture

At repository commit `7d4fcb541ad36385d2ff627ffdcbafde4f95659f`, the implementation contains:

- a domain-agnostic harness engine in .NET, Python, and Rust;
- a shared JSON envelope carried by command-line argument or `.harness/inbox.json`;
- a `TaskRegistry` that dispatches commands and applies step, instruction-size, and timeout guards;
- persistent state, feature, run-configuration, artifact, score, inbox, and trace stores;
- deterministic validators and evaluators;
- a development flow:

```text
start -> plan -> [bearings -> smoke -> pick -> implement -> verify -> handoff]* -> stop
```

- deterministic feature ordering with priorities and dependency edges;
- optional automated verification through target-owned `verify-feature.sh`;
- automated progress recording and Git commit;
- driver adapters for Codex, Claude Code, GitHub Copilot, and Devin;
- packaging for .NET, Python, and Rust engines;
- local session-usage and cost correlation for supported drivers; and
- deterministic unit and end-to-end smoke tests.

### 4.2 Verified baseline

The following checks passed on 2026-07-26:

| Engine | Unit/integration tests | End-to-end smoke |
|---|---:|---|
| .NET | 155 passed | Passed |
| Python | 158 passed | Passed |
| Rust | 143 passed: 125 engine + 18 flow | Passed |

These results establish functional regression evidence for the implemented behaviors. They do not establish the security conformance defined by this RFC.

### 4.3 Existing risk-reducing properties

The current implementation already provides useful foundations:

- workflow transitions are encoded in ordinary code;
- unknown and malformed commands receive corrective protocol errors;
- feature dependencies are cycle-checked during planning;
- global and per-feature iteration limits bound some failure loops;
- a configured per-step timeout is capped at five minutes;
- an environment variable can override the workspace-controlled timeout;
- stdout is reserved as the protocol channel and diagnostics use stderr;
- Git subprocesses use argument arrays instead of shell interpolation;
- verification subprocess output is captured and time-bounded;
- traces distinguish normal instructions, protocol errors, budget exits, and timeout exits;
- final state and traces are snapshotted;
- `.harness` is excluded from automated Git staging; and
- each engine has broad test coverage plus an actual command-line smoke test.

### 4.4 Material assurance gaps

The repository baseline has the following material gaps:

| Gap | Repository evidence | Consequence |
|---|---|---|
| Policy is mutable by the supervised workspace | `harness.json` is resolved from the current working directory | The agent or target can change most limits |
| Filesystem scope is not constrained | `targetDir` may resolve to any absolute or relative path | The run can write or commit outside the intended target |
| Target code verifies itself | `verify-feature.sh` is created and modified in the target | A compromised change can weaken its own acceptance test |
| High-impact effects are auto-approved | package and workspace settings enable terminal auto-approval or permission bypass | Deterministic flow can execute with excessive authority |
| Git commit is automatic | handoff runs `git add -A` and `git commit` | Secrets, unintended files, or manipulated changes can be committed |
| Git hooks are not neutralized | ordinary `git commit` is used | Target-controlled hooks may execute with harness authority |
| State and evidence are mutable and unsigned | JSON/JSONL files use direct writes and copies | Tampering, replay, truncation, and equivocation may be undetectable |
| State writes are not transactional | stores write destination files directly | Crash or concurrency can corrupt or lose state |
| No run identity or anti-replay fields exist | envelope contains `type`, `value`, `args`, and optional context | Stale or cross-run messages can be replayed |
| Prompt inputs lack trust labeling | all top-level `docs/*.md` and `docs/*.txt` are concatenated; skills are inlined | Prompt injection and source confusion are not contained |
| Evidence can contain sensitive local transcripts | report scripts read local Codex, Claude, and Copilot session data | Privacy and confidentiality depend on operator discipline |
| Release provenance is absent | no tracked CI workflow, SBOM, signed release manifest, or verification policy | Consumers cannot verify release origin or control execution |
| Cross-engine semantics are not fully specified | string length, path, error ordering, timestamp, and timeout behavior depend on runtime | Protocol-compatible engines can produce different evidence |
| Persistence errors usually fail open | many stores log an error and return an empty/default value | A control or evidence failure may be mistaken for a valid empty state |

## 5. Risk Model

### 5.1 Risk-rating method

Each risk SHALL be rated for likelihood and impact using the organization's approved method. If no method exists, implementations SHOULD use a five-level ordinal scale and record:

- affected assets and stakeholders;
- threat event;
- preconditions;
- likelihood;
- impact;
- inherent risk;
- selected treatment;
- control owner;
- residual risk;
- evidence;
- review date; and
- approval authority.

Risk acceptance MUST be explicit, time-bounded, and attributable to an authorized person. Absence of a control MUST NOT be represented as acceptance.

### 5.2 Threat actors and failure sources

The threat model includes:

- a compromised, manipulated, or simply mistaken model;
- prompt injection in briefs, source files, logs, issues, READMEs, or skills;
- a malicious or compromised target repository;
- a compromised package, compiler, interpreter, model, driver, or service provider;
- a local process racing or altering `.harness` artifacts;
- an operator granting excessive permissions;
- an agent weakening tests, policy, or evidence;
- replay of a valid envelope;
- stale state from a prior run;
- cross-engine behavioral divergence;
- accidental secret or personal-data capture;
- malformed or adversarially large inputs;
- subprocesses that spawn descendants or outlive timeouts; and
- crashes during state, trace, package, or release writes.

### 5.3 Initial risk register

| ID | Risk | Initial rating | Required treatment |
|---|---|---|---|
| IAO-R01 | Prompt injection changes the effective instruction hierarchy | Critical | Input provenance, trust labeling, policy isolation, adversarial TEVV |
| IAO-R02 | Agent-selected target path escapes the authorized root | Critical | Canonical path containment and broker enforcement |
| IAO-R03 | Agent-controlled verifier falsely approves agent-controlled changes | Critical | Independent verifier and protected test baseline |
| IAO-R04 | State or trace tampering conceals an unsafe or incomplete run | High | Transactional state, hash chaining, signatures, protected evidence storage |
| IAO-R05 | Auto-approved terminal or Git effects exceed intended authority | High | Capability policy, risk-tiered approvals, hook neutralization |
| IAO-R06 | Session reports disclose prompts, source, credentials, or personal data | High | Opt-in collection, minimization, redaction, retention, access control |
| IAO-R07 | Compromised dependencies or packages enter a release | High | Locked dependencies, SBOM, provenance, scanning, signed releases |
| IAO-R08 | Concurrent drivers corrupt or fork a run | High | Run lease, compare-and-swap generation, idempotency |
| IAO-R09 | Runtime differences break protocol or evidence equivalence | Moderate | Normative encoding and cross-engine conformance suite |
| IAO-R10 | Workspace changes disable budgets or increase timeouts | High | External, immutable policy authority |
| IAO-R11 | A failed persistence operation is treated as an empty valid state | High | Fail-closed control-plane error semantics |
| IAO-R12 | Green functional tests conceal missing adversarial security tests | High | Security test plan, red-team corpus, independent assessment |

## 6. Trustworthy Execution Architecture

### 6.1 Separation of planes

A conforming controlled deployment MUST separate four planes:

1. **Policy plane** — run policy, approvals, trusted keys, allowed roots, capability rules, provider rules, retention, and risk tolerances.
2. **Control plane** — state machine, validators, leases, state, decision records, and evidence generation.
3. **Exchange plane** — untrusted agent messages and instructions.
4. **Target plane** — source, build files, target scripts, generated code, and Git working tree.

The supervised agent MUST NOT have write access to the policy plane. It SHOULD NOT have direct write access to control-plane state or evidence. If an implementation cannot enforce this with operating-system permissions, it MUST use a separate broker process or service that validates every mutation and it MUST document the residual risk.

The current `.harness` layout MAY remain as a compatibility exchange directory, but controlled state and authoritative evidence MUST reside in a protected control root.

### 6.2 Policy authority

Every run MUST bind to an immutable policy manifest before the first agent instruction is issued.

The policy manifest MUST include:

- policy schema and version;
- policy identifier and digest;
- run mode and conformance level;
- one or more canonical target roots;
- exchange, control, evidence, and temporary roots;
- allowed drivers and model/provider constraints;
- allowed capabilities and per-capability parameters;
- network-egress policy;
- subprocess timeout and resource ceilings;
- step, instruction, output, file-count, and byte budgets;
- approval rules;
- verification profile;
- evidence-retention class;
- privacy classification;
- incident contact;
- policy authority and signature metadata; and
- expiry or review date.

Workspace configuration MAY further restrict a policy, but MUST NOT expand it. The effective value for every ceiling MUST be the most restrictive value from all applicable sources.

An invalid, missing, expired, or unverifiable mandatory policy MUST stop the run with a control-plane failure. It MUST NOT silently fall back to permissive defaults.

### 6.3 Path authorization

Before any filesystem or Git effect, the capability broker MUST:

1. reject an empty target;
2. resolve the requested path to an absolute canonical path;
3. resolve symlinks for every existing path component;
4. reject traversal outside an authorized canonical root;
5. re-check containment immediately before the effect;
6. reject a root that is broader than policy permits;
7. apply the same check to temporary, log, artifact, and Git paths; and
8. record the canonical path and authorization decision.

Lexical prefix checks alone are insufficient. A target of `/`, a home directory, the IAO distribution directory, or an unresolved parent MUST be rejected unless a separately approved policy explicitly authorizes that exact root.

### 6.4 Run identity, leases, and replay protection

Each run MUST have a globally unique `runId`. Each message MUST have a unique `messageId`, a monotonic `step`, and an `inReplyTo` relationship.

The control plane MUST reject:

- a message for a different run;
- a duplicate `messageId`, except as an idempotent retry returning the recorded result;
- a step lower or higher than the expected step;
- an expired message;
- a message that does not reply to the outstanding instruction;
- a message from an unauthorized driver; and
- a message whose integrity validation fails.

Only one writer lease MAY control a run at a time. The lease MUST have an owner, generation, acquisition time, expiry, and renewal rule. Lease takeover MUST be recorded and MUST require either expiry or authorized intervention.

### 6.5 Versioned envelope protocol

The authoritative protocol MUST be versioned. Version 1 messages SHOULD use the following logical shape:

```json
{
  "schema": "iao.envelope/v1",
  "runId": "019b1ed0-6bea-7bc1-a790-0bdb42bb8ab6",
  "messageId": "019b1ed0-6c13-74e6-9be1-416d04c5cda3",
  "inReplyTo": "019b1ed0-6bff-78ef-bc1b-58239493b0a9",
  "step": 7,
  "issuedAt": "2026-07-26T15:03:11.482Z",
  "expiresAt": "2026-07-26T15:08:11.482Z",
  "kind": "command",
  "command": "implement",
  "args": ["Implemented bounded input validation."],
  "actor": {
    "driver": "codex",
    "instance": "local-workstation"
  },
  "policyDigest": "sha256:...",
  "contextDigest": "sha256:...",
  "integrity": {
    "profile": "iao-integrity-v1",
    "keyId": "local-run-key-2026-07",
    "value": "base64url..."
  }
}
```

Protocol requirements:

- JSON MUST be UTF-8.
- JSON used for digests or signatures MUST be canonicalized using RFC 8785.
- `schema`, `runId`, `messageId`, `step`, `kind`, `command`, and `policyDigest` MUST be present.
- Unknown fields MUST be rejected in strict mode and MAY be retained in an explicit extension object in compatibility mode.
- Field types MUST be validated; implicit string conversion MUST NOT occur.
- Each field and message MUST have a configured size limit.
- The `kind` value MUST match the command's registered contract.
- Validation errors MUST be machine-readable and MUST NOT rely on localized prose.
- `args` SHOULD be replaced by named, command-specific payload objects in a future protocol revision.

The legacy `{type,value,args,context}` envelope MAY be accepted only in compatibility mode. Compatibility mode MUST be visibly reported in evidence and MUST NOT qualify for the Assured conformance level.

### 6.6 Message and evidence integrity

Controlled deployments MUST protect control-plane messages and evidence with at least:

- SHA-256 digests;
- a keyed message-authentication mechanism whose key is unavailable to the agent; and
- a hash chain across ordered audit events.

Assured deployments MUST use a digital signature profile approved by organizational cryptographic policy. Federal deployments MUST use applicable FIPS-approved algorithms and validated cryptographic modules where required.

Keys MUST NOT be stored in the target or exchange plane. Key rotation, revocation, backup, and destruction MUST be documented. A signature or digest failure MUST fail closed.

### 6.7 Transactional state

Authoritative state MUST be:

- schema-versioned;
- scoped by `runId`;
- written atomically;
- protected by a lease or lock;
- generation-numbered;
- integrity-protected;
- backed up or journaled according to policy; and
- recoverable to the last committed generation.

An atomic file implementation MUST write to a same-filesystem temporary file, flush file data, atomically rename, and, where supported, flush the containing directory. The implementation MUST verify the new generation before acknowledging success.

Control-plane persistence failure MUST produce a terminal `control_failure` state. Returning an empty state, empty feature list, or default policy after a mandatory store failure is prohibited.

Trace appends MUST be atomic at the event level. Snapshot creation MUST bind the snapshot to its source run, last event hash, policy digest, and state digest.

### 6.8 Prompt assembly and input provenance

All prompt inputs MUST be classified as one of:

- system policy;
- trusted flow instruction;
- approved skill;
- user-supplied objective;
- repository content;
- tool output;
- model output; or
- external third-party content.

Only system policy and trusted flow instructions MAY define instruction precedence. Repository files, logs, briefs, issues, code comments, generated text, and external content MUST be presented as untrusted data.

The prompt assembler MUST:

- use an explicit active-input manifest instead of indiscriminately ingesting all documents in a directory;
- record source path, media type, byte count, digest, trust class, and truncation;
- apply per-source and aggregate byte limits;
- reject unsupported encodings, devices, sockets, and unauthorized symlinks;
- escape or structurally isolate untrusted content;
- state that instructions found in untrusted content do not supersede policy;
- include only allowlisted skills whose digests match the policy;
- record the final prompt manifest digest; and
- test the assembly process against a maintained prompt-injection corpus.

Truncation MUST occur on a defined UTF-8 boundary and MUST be represented in evidence. A truncated source MUST NOT be cited as fully reviewed.

### 6.9 Capability broker and least privilege

The agent MUST obtain effects through named capabilities. Direct unrestricted shell, filesystem, network, credential, or Git access is non-conforming for Controlled and Assured deployments.

At minimum, capabilities MUST distinguish:

- repository read;
- bounded file write;
- bounded file delete;
- build;
- test;
- package restore;
- network fetch;
- Git inspect;
- Git stage;
- Git commit;
- Git push;
- artifact publish;
- secret access; and
- external message or service action.

Each request MUST include the run, step, capability, parameters, reason, affected root, and expected evidence. The broker MUST validate parameters independently of the model's explanation.

Network access MUST be denied by default. When allowed, policy MUST constrain protocol, destination, port, method, credential, request size, response size, and purpose. Redirects and DNS rebinding MUST be handled according to policy.

Subprocesses MUST run with:

- a minimal environment;
- no unnecessary credentials;
- a controlled working directory;
- output and duration limits;
- process-tree termination;
- resource limits where supported;
- network restrictions appropriate to the capability; and
- captured exit status and evidence.

### 6.10 Human authorization

Effects MUST be classified:

| Class | Example | Default |
|---|---|---|
| A0 — observational | Read source, inspect Git status, calculate digest | May be automatic |
| A1 — bounded reversible | Edit files inside approved target, run offline build | May be pre-authorized by signed policy |
| A2 — consequential | Change dependencies, access network, commit, alter tests or CI | Explicit approval or narrowly scoped signed pre-authorization |
| A3 — external or difficult to reverse | Push, publish, deploy, delete material data, use production secrets, send external messages | Explicit approval at time of action |

Permission-bypass and terminal auto-approval settings MUST NOT be emitted as the default configuration of a conforming package. If an organization elects unattended operation, its signed policy MUST enumerate the exact A1 and A2 capabilities pre-authorized, their scope, ceilings, and compensating controls.

Approval records MUST include approver identity, decision, timestamp, request digest, policy digest, scope, and expiry.

### 6.11 Git controls

Git operations MUST be scoped to a canonical authorized repository root.

Before commit, the broker MUST:

- neutralize untrusted Git hooks, for example through a trusted hooks path;
- prevent use of an untrusted pager, editor, signing program, credential helper, or external diff;
- inspect staged paths for boundary escape and unexpected submodules;
- run secret and sensitive-data scanning;
- present or record a bounded diff summary;
- bind the commit to verification evidence;
- require A2 authorization unless the policy explicitly pre-authorizes commits; and
- record the resulting full commit hash.

Automated staging MUST NOT use an unconstrained repository-wide `git add -A`. Pathspecs MUST be derived from the authorized change set. A dirty state outside that set MUST be reported and MUST NOT be silently absorbed.

Git push, tag creation, release publication, and force operations are A3 effects.

### 6.12 Independent verification

A target-owned script that the agent can create or modify MUST NOT be the sole basis for marking a feature complete.

Controlled verification MUST include:

- a protected verification manifest created before or independently of the implementation step;
- a trusted command or test entry point;
- a clean or attestably isolated environment;
- a source revision and diff digest;
- dependency and toolchain identity;
- exit status and bounded logs;
- a check that protected tests and policy were not weakened;
- a check for unexpected file or dependency changes; and
- a signed or keyed verdict.

Assured verification MUST be performed by an actor or service separated from the implementing agent. The verifier MUST NOT inherit the implementing agent's conversational context as authority.

A successful exit code from an agent-modifiable script MAY be retained as diagnostic evidence but MUST be labeled `self_attested`. It cannot satisfy independent TEVV.

### 6.13 Audit and evidence

The audit stream MUST record, at minimum:

- schema version;
- event ID;
- run ID and step;
- timestamp from a trusted or documented clock source;
- previous-event hash;
- policy and implementation digests;
- driver, model, and provider identifiers when available;
- instruction and response digests;
- command and validator result;
- capability request and authorization decision;
- canonical affected resources;
- approval reference;
- subprocess identity, status, and timeout;
- state generation and digest;
- verification verdict and evidence reference;
- exception or failure class; and
- retention class.

Raw prompts, model responses, source content, logs, and local transcripts MUST NOT be copied into the authoritative audit stream by default. Evidence SHOULD use digests and separately protected references. When raw content is necessary, the collection purpose, data classification, access, retention, and disposal MUST be defined.

Audit events MUST use RFC 3339 UTC timestamps with a consistent precision. Ordered decisions MUST also use monotonic step and generation values because wall-clock time alone is insufficient for ordering.

### 6.14 Privacy and sensitive data

Before enabling session-usage or cost reporting, an operator MUST opt in to the specific local data sources to be read.

Report tooling MUST:

- identify each source and purpose;
- default to repository-scoped collection;
- minimize fields before aggregation;
- avoid copying prompt or source bodies unless explicitly requested;
- redact credentials, tokens, email addresses, and configured sensitive patterns;
- separate cost metrics from content;
- apply access control to generated reports;
- define retention and secure disposal;
- disclose estimation uncertainty; and
- provide a content-free summary mode.

Session IDs and filesystem paths SHOULD be pseudonymized in shareable reports. Reports containing model/provider telemetry MUST be classified at least as internal unless reviewed for release.

### 6.15 Supply-chain and release controls

The project MUST establish a release process that:

- pins compiler, SDK, interpreter, and package-manager versions;
- uses dependency lock data for every shipped engine;
- inventories direct and transitive dependencies;
- generates a standards-based SBOM;
- performs vulnerability, license, secret, and malicious-package checks;
- builds in a protected CI environment;
- archives build, test, scan, and provenance evidence;
- signs release artifacts and publishes verification information;
- records any Native AOT fallback as a different build profile;
- verifies that driver adapters and generated approval settings match policy;
- tests the installed package, not only the source tree; and
- defines vulnerability intake, triage, remediation, and disclosure.

Packages MUST NOT silently weaken approval controls. A package generated for a specific driver MUST declare the permissions it expects before installation.

### 6.16 Cross-engine interoperability

The .NET, Python, and Rust engines MUST implement the same normative semantics.

The specification defines:

- instruction and output size in UTF-8 octets, not runtime-specific string length;
- canonical JSON using RFC 8785 where equality or integrity matters;
- timestamps as RFC 3339 UTC;
- deterministic error codes and field paths;
- deterministic command ordering;
- canonical path-containment behavior;
- the same timeout outcome and process-tree semantics;
- the same feature-ID uniqueness requirements;
- the same dependency-graph validation;
- the same fail-closed persistence behavior; and
- byte-for-byte compatible authoritative evidence after canonicalization.

The conformance suite MUST exchange state and envelopes across engines. At least one scenario MUST begin in each engine and resume in each other engine.

## 7. Normative State Machine

### 7.1 Run states

The control plane MUST distinguish:

- `created`;
- `policy_validated`;
- `planning`;
- `ready`;
- `implementing`;
- `verifying`;
- `awaiting_approval`;
- `committing`;
- `completed`;
- `cancelled`;
- `budget_exhausted`;
- `timed_out`;
- `control_failure`;
- `security_hold`; and
- `incident_hold`.

The literal `stop` is insufficient as an authoritative terminal result because it conflates success, budget exhaustion, timeout, blocked dependencies, and failure. Compatibility adapters MAY still print `stop`, but MUST persist and expose the typed terminal state.

### 7.2 Required transition rules

1. A run MUST NOT leave `created` until policy and roots are validated.
2. Planning output MUST pass schema, uniqueness, dependency, size, and scope validation.
3. A feature MUST NOT enter `implementing` until its dependencies are independently verified.
4. Verification MUST bind to the exact source and policy digests under review.
5. A feature MUST NOT be marked passing solely from agent self-report.
6. A commit MUST NOT occur before the required approval and verifier verdict.
7. Completion MUST require every required feature to have a valid evidence reference.
8. Budget exhaustion, timeout, blocked dependencies, and persistence failure MUST NOT be represented as successful completion.
9. Resume MUST validate policy continuity, lease ownership, state integrity, and the target revision.
10. Any integrity failure MUST transition to `security_hold` or `incident_hold`.

### 7.3 Budget semantics

Policy MUST define:

- maximum run steps;
- maximum corrective retries;
- maximum steps per feature;
- maximum prompt and response bytes;
- maximum cumulative model usage where independently measurable;
- maximum subprocess duration;
- maximum total wall-clock duration;
- maximum filesystem changes;
- maximum network requests and bytes; and
- maximum cost where provider telemetry supports reliable enforcement.

Instruction characters are a useful local proxy but MUST NOT be described as model-token or monetary cost. Provider-derived cost estimates MUST record price-source version, service tier, context-rate assumptions, and unpriced usage.

## 8. Control Requirements

### 8.1 Governance controls

| Requirement | Normative outcome |
|---|---|
| IAO-GV-01 | The organization MUST assign a system owner, risk owner, security owner, privacy owner, model/provider owner, release authority, and incident contact. |
| IAO-GV-02 | Intended use, prohibited use, users, affected stakeholders, operational context, and risk tolerance MUST be documented. |
| IAO-GV-03 | The organization MUST maintain an inventory of engines, flows, drivers, models, providers, skills, dependencies, and external services. |
| IAO-GV-04 | Exceptions MUST be attributable, justified, time-bounded, reviewed, and linked to compensating controls. |
| IAO-GV-05 | Material model, provider, flow, capability, policy, or target-context changes MUST trigger risk review. |
| IAO-GV-06 | An acceptable-use policy MUST define disallowed code, data, targets, and effects. |
| IAO-GV-07 | Third-party terms, data use, retention, incident notification, and service changes MUST be reviewed. |
| IAO-GV-08 | Independent assessment depth MUST be proportional to deployment risk. |

### 8.2 Protection controls

| Requirement | Normative outcome |
|---|---|
| IAO-PR-01 | Agent authority MUST be least privilege and capability-based. |
| IAO-PR-02 | Policy and keys MUST be unavailable for agent modification. |
| IAO-PR-03 | Target, temporary, log, evidence, and Git paths MUST be canonically contained. |
| IAO-PR-04 | Network egress and secret access MUST be denied by default. |
| IAO-PR-05 | Control state MUST be atomic, generation-checked, and integrity-protected. |
| IAO-PR-06 | Messages MUST be authenticated and replay-resistant. |
| IAO-PR-07 | Prompt sources and skills MUST have provenance, trust class, and digest. |
| IAO-PR-08 | High-impact effects MUST follow the approval matrix. |
| IAO-PR-09 | Sensitive telemetry MUST be minimized, access-controlled, and retained by policy. |
| IAO-PR-10 | Release artifacts MUST have SBOM, provenance, integrity information, and signatures. |

### 8.3 Detection and measurement controls

| Requirement | Normative outcome |
|---|---|
| IAO-DE-01 | Every control decision and effect MUST produce a structured audit event. |
| IAO-DE-02 | Audit continuity and integrity MUST be verifiable. |
| IAO-DE-03 | Cross-engine conformance MUST be continuously tested. |
| IAO-DE-04 | Security tests MUST cover prompt injection, path escape, replay, verifier tampering, secret leakage, and concurrency. |
| IAO-DE-05 | Provider/model performance claims MUST be based on documented empirical evaluation. |
| IAO-DE-06 | Risk and control metrics MUST be reviewed at a defined cadence. |
| IAO-DE-07 | Vulnerability and dependency intelligence MUST be monitored. |
| IAO-DE-08 | Verification evidence MUST be tied to the exact source, policy, toolchain, and dependency set. |

### 8.4 Response and recovery controls

| Requirement | Normative outcome |
|---|---|
| IAO-RS-01 | The project MUST publish a vulnerability and AI-incident reporting channel. |
| IAO-RS-02 | Integrity failures, unauthorized effects, secret exposure, and unsafe model behavior MUST have defined escalation paths. |
| IAO-RS-03 | Run suspension, lease revocation, key revocation, artifact quarantine, and evidence preservation MUST be supported. |
| IAO-RS-04 | Recovery MUST begin from a verified state and target revision. |
| IAO-RS-05 | Incidents MUST receive root-cause analysis and preventive-action tracking. |
| IAO-RS-06 | Third-party model, driver, or package failure MUST have a documented fallback or shutdown strategy. |

## 9. NIST Crosswalk

This crosswalk is informative evidence of alignment. It is not a substitute for organizational control selection, tailoring, implementation statements, or assessment.

| IAO outcome | AI RMF / GenAI Profile | NIST SP 800-53 Rev. 5 | NIST SSDF 1.1 |
|---|---|---|---|
| Defined purpose, context, users, and limits | MAP 1.1; MAP 2.2; GV-5.1-002 | PL-2; RA-3 | PO.1.1; PO.1.2; PW.1.1 |
| Roles and accountability | GOVERN 2; GV-2.1-001; GV-3.2-002 | PM-2; PM-13; PL-2 | PO.2 |
| Acceptable use and human oversight | GOVERN 3; GV-3.2-003; GV-3.2-004 | AC-2; PL-4 | PO.1; PO.2 |
| Threat modeling | MAP; GV-3.2-005 | RA-3; SA-8 | PW.1.1; PW.1.2 |
| Independent evaluation | MEASURE; GV-3.2-001; MS-1.3-003; MS-2.3-003 | CA-2; CA-7; SA-11 | PW.7; PW.8 |
| Model/provider and third-party inventory | GOVERN 1.6; GV-6.1-007 | CM-8; SR-3 | PO.1.3; PW.4 |
| Supplier due diligence and agreements | GOVERN 6; GV-6.1-004; GV-6.1-005; GV-6.1-009 | SA-4; SR-3; SR-5 | PO.1.3; PW.4.4 |
| Third-party incident planning | GV-6.2-003; GV-6.2-004 | IR-4; IR-6; SR-8 | RV.1; RV.2 |
| Least privilege and access enforcement | GOVERN; MANAGE | AC-3; AC-6; CM-5 | PO.5; PS.1.1 |
| Protected development/control environment | MANAGE | CM-2; CM-6; SA-15 | PO.3; PO.5 |
| Prompt and input provenance | MAP; MS-2.2-002; MS-2.5-003; MS-2.5-005 | SI-7; SI-10; SR-4 | PS.3.2; PW.1 |
| Message, state, and evidence integrity | MEASURE 2.7; MS-2.7-004; MS-2.7-009 | AU-9; SC-13; SI-7 | PO.3.1; PS.1; PS.2 |
| Event logging and trace review | GOVERN 1.5; MEASURE; MANAGE | AU-2; AU-3; AU-6; AU-8; AU-12 | PO.3.3; RV.1 |
| Privacy-preserving telemetry | MAP; MS-2.2-002; MP-4.1-009 | PT-2; PT-3; SI-12 | PO.1; PS.1 |
| Empirical capability evaluation | MEASURE; MS-2.3-002; MS-2.5-001 | CA-2; SA-11 | PW.8 |
| Adversarial and prompt-injection testing | MS-2.7-007; MS-4.2-001; MS-4.2-002 | CA-8; SA-11; SI-10 | PW.7; PW.8 |
| Risk treatment and go/no-go | MANAGE 1.3; MG-1.3-001; MS-4.2-005 | CA-6; PM-9; RA-7 | PW.1.2 |
| Continuous control monitoring | GOVERN 1.5; MG-1.3-002 | CA-7; SI-4 | PO.3.2; RV.1 |
| Release integrity | MANAGE; GV-6.1-008 | SA-10; SI-7; SR-4 | PS.2.1; PS.3.1 |
| SBOM and component provenance | GOVERN 6; MAP | CM-8; SA-10; SR-4 | PS.3.2; PW.4.1 |
| Secure design and tracked decisions | MAP; MANAGE | SA-3; SA-8; SA-17 | PW.1.1; PW.1.2 |
| Code review and executable testing | MEASURE | SA-11; SA-15 | PW.7.1; PW.7.2; PW.8.1; PW.8.2 |
| Secure defaults | MANAGE | CM-2; CM-6 | PW.9.1; PW.9.2 |
| Vulnerability intake and remediation | MANAGE 4 | IR-4; SI-2 | RV.1; RV.2; RV.3 |
| System planning and control status | GOVERN | PL-2; CA-2; CA-7 | PO.1; PO.4 |

## 10. Conformance Levels

### 10.1 IAO Core

IAO Core demonstrates functional protocol conformance:

- versioned schema;
- deterministic state transitions;
- typed terminal outcomes;
- bounded execution;
- persistent state;
- trace generation; and
- cross-engine conformance tests.

IAO Core is suitable for experimentation. It is not a security-conformance claim.

### 10.2 IAO Controlled

IAO Controlled requires all Core requirements plus:

- immutable external policy authority;
- canonical root containment;
- capability broker;
- least-privilege subprocess environment;
- run identity, lease, idempotency, and replay protection;
- atomic state and hash-chained audit;
- protected prompt/skill provenance;
- approval classes;
- protected verification manifest;
- privacy-controlled telemetry;
- CI security tests;
- SBOM and release integrity information; and
- documented risk register and incident process.

### 10.3 IAO Assured

IAO Assured requires all Controlled requirements plus:

- digitally signed evidence and releases;
- cryptographic keys protected outside the agent boundary;
- independent implementing and verifying actors;
- isolated verification with restricted network and credentials;
- reproducible or independently repeated builds;
- adversarial TEVV and documented results;
- formal control assessment using tailored SP 800-53A procedures;
- no legacy envelope mode;
- no permission-bypass defaults;
- no unresolved Critical or High risks without approved, time-bounded exceptions; and
- approval-authority acceptance of residual risk.

### 10.4 Conformance statement

A conformance statement MUST identify:

- RFC version;
- implementation commit and release;
- engine and driver;
- conformance level;
- deployment boundary;
- policy identifier and digest;
- applicable exceptions;
- test-suite version and results;
- assessment date;
- assessor;
- evidence-manifest digest; and
- expiry or next review date.

The statement MUST say “NIST-aligned” rather than “NIST-certified” unless a separate, valid certification or authorization basis exists.

## 11. Assessment Procedures

Assessment SHALL use examine, interview, and test methods consistent with the tailoring principles of NIST SP 800-53A Rev. 5.

### 11.1 Minimum evidence

An assessor MUST examine:

- system and data-flow description;
- policy manifests;
- risk register and exceptions;
- source and release digests;
- dependency locks and SBOM;
- CI and test results;
- cross-engine conformance results;
- capability and approval policies;
- audit and evidence manifests;
- privacy and retention policy;
- incident and vulnerability procedures;
- model/provider inventory and terms;
- verifier separation; and
- at least one complete run.

### 11.2 Required adversarial tests

The conformance suite MUST include:

1. prompt injection in a brief;
2. prompt injection in a skill;
3. prompt injection in source comments and logs;
4. absolute-path escape;
5. `..` traversal;
6. symlink escape and symlink swap;
7. target root `/` and home-directory rejection;
8. stale message replay;
9. duplicate message idempotency;
10. wrong-run and wrong-step messages;
11. state corruption and interrupted atomic write;
12. concurrent writer and lease takeover;
13. trace truncation, insertion, and reordering;
14. policy modification attempt;
15. verifier script modification;
16. test deletion or weakening;
17. malicious Git hook;
18. unexpected dependency addition;
19. secret introduced into the staged diff;
20. subprocess timeout with descendants;
21. output-flood and oversized-envelope handling;
22. network-deny and egress-allowlist enforcement;
23. transcript-report redaction;
24. cross-engine resume in all engine pairs; and
25. terminal-state distinction among success, timeout, budget, blocked, cancelled, and control failure.

### 11.3 Acceptance metrics

At minimum:

- 100% of mandatory protocol tests MUST pass;
- 100% of path-escape tests MUST be denied;
- 100% of stale or cross-run messages MUST be rejected;
- 100% of authoritative audit tampering MUST be detected;
- no A2 or A3 effect MAY occur without the required authorization;
- independent verification MUST detect protected-test modification;
- no known Critical vulnerability MAY remain open at release;
- High vulnerabilities MUST be remediated or covered by approved exceptions;
- cross-engine canonical evidence MUST match;
- all release files MUST verify against published integrity data; and
- privacy tests MUST show that default reports contain no prompt or source bodies.

## 12. Implementation and Migration Plan

### Phase 0 — Specification and freeze

Deliver:

- protocol schemas;
- typed terminal states;
- canonical encoding rules;
- control and evidence directory model;
- initial risk register;
- conformance-test repository; and
- architecture decision records for trust boundaries.

Exit criteria:

- schemas review cleanly across all three languages;
- legacy behaviors are inventoried;
- no new protocol field is added without versioning.

### Phase 1 — Control-plane integrity

Deliver:

- external signed policy manifest;
- run identity and lease;
- replay protection;
- canonical path authorization;
- atomic state store;
- hash-chained audit events; and
- fail-closed persistence.

Exit criteria:

- IAO-R02, IAO-R04, IAO-R08, IAO-R10, and IAO-R11 have tested mitigations.

### Phase 2 — Capability and approval enforcement

Deliver:

- capability broker;
- minimal subprocess environment;
- egress control;
- risk-tiered approval service;
- constrained Git operations;
- hook neutralization; and
- removal of permission-bypass packaging defaults.

Exit criteria:

- every effect is attributable to a capability decision;
- no A2 or A3 test bypasses policy.

### Phase 3 — Independent TEVV and prompt provenance

Deliver:

- active-input manifest;
- skill allowlist and digests;
- prompt trust labeling;
- protected verification manifest;
- independent verifier;
- test-integrity checks; and
- adversarial prompt-injection suite.

Exit criteria:

- IAO-R01 and IAO-R03 have tested mitigations;
- self-attested verification cannot mark a feature complete.

### Phase 4 — Privacy and supply chain

Deliver:

- opt-in, minimized session reporting;
- redaction and retention controls;
- CI workflows;
- pinned toolchains and complete lock strategy;
- SAST, SCA, secret, and license gates;
- SBOM;
- signed release and provenance manifest; and
- vulnerability disclosure process.

Exit criteria:

- IAO-R06 and IAO-R07 have tested mitigations;
- a downstream consumer can verify a package without trusting the download channel.

### Phase 5 — Assured conformance

Deliver:

- cross-engine resume matrix;
- byte-level canonical evidence parity;
- isolated verifier;
- independent build repetition;
- SP 800-53A-tailored assessment plan and report;
- plan of action and milestones for residual findings; and
- approval-authority decision.

Exit criteria:

- all Assured requirements pass;
- no unapproved Critical or High residual risk remains.

## 13. Backward Compatibility

The existing runners and `.harness/inbox.json` transport MAY be retained during migration.

Compatibility requirements:

- the legacy parser MUST be disabled by default at the Assured level;
- importing legacy state MUST never overwrite authoritative state;
- import MUST create a new `runId`, preserve source digests, and label provenance as legacy;
- the current textual stdout instruction MAY wrap a versioned machine-readable response;
- the current `stop` output MAY remain for adapters but MUST map to a typed terminal record;
- existing trace and report readers SHOULD support both legacy and v1 events;
- old state without integrity data MUST be treated as unverified; and
- policy MUST define the sunset date for compatibility mode.

## 14. Operational Requirements

### 14.1 Before a run

The operator or service MUST:

- select and validate policy;
- identify target and canonical root;
- select driver, model, provider, and engine;
- verify implementation and skill digests;
- obtain required pre-authorization;
- confirm evidence storage and retention;
- confirm verifier availability; and
- create the run lease.

### 14.2 During a run

The control plane MUST:

- monitor budgets and policy;
- validate every message and effect;
- preserve audit continuity;
- stop on integrity or control failure;
- surface approval requests;
- keep sensitive content outside the main audit stream; and
- allow authorized cancellation.

### 14.3 After a run

The control plane MUST:

- finalize the typed terminal state;
- close the lease;
- verify audit and state digests;
- generate an evidence manifest;
- record verifier and approval results;
- apply retention and disposal;
- quarantine failed or suspicious outputs; and
- prevent release unless the selected gate passes.

## 15. Security Considerations

Deterministic sequencing does not imply secure execution. A deterministic harness can repeat an unsafe action more reliably than an unconstrained agent if its policy, inputs, verifier, or capabilities are compromised.

The most important security boundary is therefore not between “prompt” and “code”; it is between the supervised agent/target and the policy, state, capability enforcement, verification, and evidence that judge the run.

Implementers MUST account for:

- confused-deputy behavior;
- prompt injection;
- path traversal and symlink races;
- replay and stale state;
- test and policy tampering;
- subprocess escape and descendants;
- malicious Git configuration and hooks;
- dependency and model supply-chain compromise;
- secret exposure;
- evidence equivocation;
- denial of service through budgets or output floods;
- unsafe recovery after partial writes; and
- authorization fatigue.

Security controls that merely instruct the model not to perform an action are not enforcement controls.

## 16. Privacy Considerations

IAO can process proprietary source code, prompts, briefs, logs, personal data, credentials, model telemetry, and local conversation history. These data can be more sensitive in aggregate than any individual file.

The system owner MUST document:

- data categories and purpose;
- legal or organizational authority;
- data locations and flows;
- provider retention and training terms;
- access roles;
- redaction;
- retention;
- deletion;
- incident notification; and
- downstream report sharing.

The system SHOULD operate on digests and bounded summaries whenever full content is not required. Privacy review MUST include the usage-report scripts because they inspect local driver histories outside the repository.

## 17. IANA Considerations

This document has no IANA actions.

## 18. Normative References

1. Bradner, S., “Key words for use in RFCs to Indicate Requirement Levels,” BCP 14, RFC 2119, March 1997. <https://doi.org/10.17487/RFC2119>
2. Leiba, B., “Ambiguity of Uppercase vs Lowercase in RFC 2119 Key Words,” BCP 14, RFC 8174, May 2017. <https://doi.org/10.17487/RFC8174>
3. Rundgren, A., Jordan, B., and S. Erdtman, “JSON Canonicalization Scheme (JCS),” RFC 8785, June 2020. <https://doi.org/10.17487/RFC8785>
4. Tabassi, E., “Artificial Intelligence Risk Management Framework (AI RMF 1.0),” NIST AI 100-1, January 2023. <https://doi.org/10.6028/NIST.AI.100-1>
5. Autio, C., et al., “Artificial Intelligence Risk Management Framework: Generative Artificial Intelligence Profile,” NIST AI 600-1, July 2024. <https://doi.org/10.6028/NIST.AI.600-1>
6. Souppaya, M., Scarfone, K., and D. Dodson, “Secure Software Development Framework (SSDF) Version 1.1,” NIST SP 800-218, February 2022. <https://doi.org/10.6028/NIST.SP.800-218>
7. Joint Task Force, “Security and Privacy Controls for Information Systems and Organizations,” NIST SP 800-53 Rev. 5, including Release 5.2.0 updates, August 2025. <https://doi.org/10.6028/NIST.SP.800-53r5>
8. Joint Task Force, “Assessing Security and Privacy Controls in Information Systems and Organizations,” NIST SP 800-53A Rev. 5, January 2022. <https://doi.org/10.6028/NIST.SP.800-53Ar5>
9. Licata, J., et al., “Developing Security, Privacy, and Cybersecurity Supply Chain Risk Management Plans for Systems,” NIST SP 800-18 Rev. 2, June 2026. <https://doi.org/10.6028/NIST.SP.800-18r2>

## 19. Informative References

1. Booth, H., et al., “Secure Software Development Practices for Generative AI and Dual-Use Foundation Models: An SSDF Community Profile,” NIST SP 800-218A, July 2024. <https://doi.org/10.6028/NIST.SP.800-218A>
2. National Institute of Standards and Technology, “The NIST Cybersecurity Framework (CSF) 2.0,” NIST CSWP 29, February 2024. <https://doi.org/10.6028/NIST.CSWP.29>
3. Joint Task Force, “Risk Management Framework for Information Systems and Organizations,” NIST SP 800-37 Rev. 2, December 2018. <https://doi.org/10.6028/NIST.SP.800-37r2>
4. Justino, Y., “Inverted Orchestration in Software Development: A Deterministic Harness and Looping Engineering under Enterprise Constraints,” Version 0.1.0, 2026. <https://doi.org/10.5281/zenodo.21421908>

## Appendix A. Repository Evidence Map

| Concern | Primary repository evidence |
|---|---|
| Pattern and intent | `README.md`, `README-ptbr.md` |
| Core protocol | `src/*/harness_engine` and `src/dotnet/Harness.Engine` envelope modules |
| Dispatch and guards | `TaskRegistry.cs`, `task_registry.py`, `task_registry.rs` |
| State and evidence | state, trace, inbox, artifact, score, feature, and run-config stores in each engine |
| Development flow | `src/*/flows_development`, `src/dotnet/Flows.Development` |
| Verification | `DevelopmentTasks.Verify.cs`, Python `tasks.py`, Rust `verify.rs` |
| Automated Git handoff | `DevelopmentTasks.Handoff.cs`, Python `tasks.py`, Rust `handoff.rs` |
| Prompt and skills | `PromptFormatter.cs`, `prompt_formatter.py`, `prompt_formatter.rs`, `.harness/skills/dev-*` |
| Driver authority | `.codex/agents`, `.claude/agents`, `.github/prompts`, `.devin/workflows` |
| Workspace approval | `.claude/settings.json`, `.vscode/settings.json`, generated settings in `package.sh` |
| Distribution | `package.sh`, `QUICKSTART.md`, `dist/` layout |
| Usage and cost telemetry | `.harness/scripts/*_usage.py`, `.harness/scripts/harness_cost_correlate.py`, `.harness/skills/session-report` |
| Functional verification | `run-checks.sh`, `run-checks-py.sh`, `run-checks-rs.sh` and test projects |

The untracked `arquitetura-v2.html` present during review was examined as explanatory material but is not part of the controlled commit baseline and is not relied upon as normative evidence.

## Appendix B. Cross-Engine Compatibility Findings

The current engines are intentionally similar, but the following behaviors require normative unification:

1. .NET `String.Length`, Python `len(str)`, and Rust `String::len()` do not measure the same unit for non-ASCII text. This RFC selects UTF-8 octets.
2. Rust command-error ordering is sorted, while .NET and Python depend on insertion order. This RFC requires error codes and deterministic ordering.
3. Timestamp formatting and precision vary by runtime. This RFC requires RFC 3339 UTC plus monotonic run ordering.
4. Path resolution and normalization differ across libraries. This RFC requires canonical containment and symlink handling.
5. Timeout implementations abandon a thread in the generic task guard, while verification uses process-level logic. This RFC requires process-tree semantics for effects.
6. Direct state writes have runtime-specific crash behavior. This RFC requires an atomic transaction algorithm.
7. Tolerant parsing behavior is not an adequate signed-message profile. This RFC requires strict canonical v1 messages.
8. The current README states that two protocol-compatible implementations exist although three engines are present. Release documentation MUST be generated or checked against the conformance inventory.

## Appendix C. Submission Checklist

Before changing this document from Proposed Standard to Accepted:

- [ ] Public review period completed.
- [ ] Security architecture review completed.
- [ ] Privacy review completed.
- [ ] Maintainer and system-owner approval recorded.
- [ ] Requirement IDs accepted into issue tracking.
- [ ] Protocol schemas published.
- [ ] Threat model reviewed by an independent party.
- [ ] NIST crosswalk reviewed for tailoring and applicability.
- [ ] Patent, trademark, copyright, and project-license review completed.
- [ ] Conformance test plan approved.
- [ ] Migration compatibility and sunset dates approved.
- [ ] Initial plan of action and milestones published.
- [ ] No claim of NIST certification appears without a separate basis.

## Appendix D. Decision Summary

This RFC makes five architectural decisions:

1. The agent and target are untrusted participants, even when the workflow is deterministic.
2. Policy, state, capability enforcement, verification, and evidence form the trusted computing boundary.
3. Target-controlled self-verification is diagnostic, not authoritative.
4. Cross-engine compatibility is a testable protocol property, not a documentation claim.
5. NIST alignment is demonstrated through explicit requirements, mappings, evidence, and assessment—not through naming or self-attestation.
