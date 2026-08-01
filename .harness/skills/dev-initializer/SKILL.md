---
name: dev-initializer
description: "inspect a greenfield or brownfield target, scaffold its development environment, and split the requested work into prioritized, verifiable features"
---

# Initialize the development run

Turn the supplied brief into an executable plan and prepare the target. Work in this order.

## 1. Classify the target

Determine whether the target is:

- **Greenfield:** no existing application. Use `app/<descriptive-name>` as the target and
  create the minimal project structure.
- **Brownfield:** an application already exists. Inspect only the metadata needed to plan the
  requested delta: the top-level layout, README, build manifests, existing docs/ADRs,
  bootstrap or verification scripts, and `progress.txt`. Reuse the established target and
  verification command. Do not inventory the whole source tree or re-plan the existing app.

## 2. Prepare Git

Initialize Git if necessary. Never work directly on `main` or `master`. Reuse an existing
non-default branch when resuming; otherwise create
`<YYYYMMDDHHMM>-<descriptive-name>`, using the current UTC timestamp.

## 3. Scaffold deterministic setup

- Ensure an idempotent `init.sh` prepares, builds, and when applicable starts the project.
- Ensure an idempotent `verify-feature.sh <id>` runs the established verification path. It
  may run the full suite when there is no per-feature convention.
- Make the verifier print a concise `PASS: ...` or `FAIL: ...` verdict and use its process
  exit code as evidence. The harness captures detailed output separately.
- In brownfield targets, reuse equivalent scripts or pipelines and make only the minimum
  adaptation needed. Do not overwrite working project setup.

## 4. Plan features

For greenfield, split the whole goal. For brownfield, split only the requested delta. Make
each feature:

- small, vertical, independently implementable, and independently verifiable;
- assigned a numeric priority where `1` is highest;
- linked through `dependsOn` only when a real implementation dependency exists;
- described objectively with enough context for a fresh session;
- linked through `references` only to explicit codes or named sections from the brief;
- populated with `implementationContext` as an object containing the bounded inline material
  the feature needs to be implemented without reopening the full brief. Use the four arrays
  `requirements`, `constraints`, `files`, and `acceptance`; copy only the relevant requirements,
  constraints, target files, examples, and acceptance criteria into those arrays.

Prefer several small features to a few broad ones. If a feature has no unambiguous
verification path, split it further.
