---
name: dev-initializer
description: "expand a brief into prioritized features and scaffold the environment"
---

# SKILL: initializer (session 0)

You turn a brief into an executable plan and prepare the ground. Do the following, in this
order:

## 0. Detect greenfield vs brownfield
Before scaffolding anything, find out whether an app already exists in the target directory —
the brief may explicitly point to an existing path, or the default target directory
(`app/<descriptive-name>`, see step 2) may already exist with real content (not empty, not
just what the harness itself created in earlier runs).

- **Greenfield** (nothing exists yet): follow steps 1-3 normally, creating everything from
  scratch.
- **Brownfield** (the app already exists): do an inspection **limited to metadata**, never the
  entire source code — README, build manifests (`package.json`, `.csproj`/`.sln`, `Makefile`,
  `Dockerfile`), top-level directory listing, existing `docs/`/ADRs and `progress.txt`, if any.
  The goal is only to:
  - confirm the **already-established** `$VERIFY_CMD` (don't invent a new one if one already
    exists);
  - understand the structure/conventions/architecture already in place, so the new features
    fit into them instead of contradicting them.
  Don't try to understand every already-implemented feature — that doesn't scale in a large
  app and contradicts the harness's lean-context principle. Ad hoc investigation of "does
  something like feature X already exist?" is the job of each feature session
  (`bearings`/`implement`), not this session 0.

## 1. Ensure a Git repository and a working branch
If the target directory isn't a Git repository (`git rev-parse --is-inside-work-tree` fails),
run `git init`. Then, ensure a branch dedicated to this development — never work directly on
`main`/`master`: if you're already on a non-default branch (e.g. resuming a previous run),
reuse it; otherwise, create and switch to a new one named
`<YYYYMMDDHHMM>-<descriptive-name>` — the prefix is the UTC timestamp at creation time (e.g.
`202607211830`) and `<descriptive-name>` reflects the project/brief's goal
(`git checkout -b <YYYYMMDDHHMM>-<descriptive-name>`).

## 2. Scaffold the environment
**Greenfield:** the target directory always follows the pattern `app/<descriptive-name>` —
just the descriptive part of the branch name from step 1, **without** the timestamp prefix.
Create it if it doesn't exist. Inside it, create:
- an **idempotent** `init.sh` that gets the project ready to run from scratch: install
  dependencies, restore/build and (if applicable) bring the app up. It must be runnable
  multiple times without breaking;
- an **idempotent** `verify-feature.sh <id>` that verifies the given feature. At first it can
  run the full suite (`./init.sh` and then `$VERIFY_CMD`), without requiring per-feature
  filtering. It must print a line starting with `PASS` when everything passes, or a line in
  the format `FAIL: <reason>` when it fails, and exit with a 0/non-zero code accordingly.
  Avoid long prose on stdout; the harness captures full stdout/stderr in
  `.harness/logs/verify-feature-<id>.log`.

Also create the project's minimal folder structure.

**Brownfield:** the target directory is wherever the app already lives (not necessarily
`app/<descriptive-name>`). **Do not** recreate or overwrite what already exists: if there's
already an `init.sh` (or an equivalent pipeline — Makefile, bootstrap script), reuse it,
adjusting only the minimum necessary for the requested change. Only create an `init.sh` from
scratch if nothing equivalent really exists. Also ensure a `verify-feature.sh <id>` in the
target directory; if there's already an equivalent verification wrapper, reuse it or build a
minimal adapter. If there's no per-feature filtering convention, the wrapper should run the
full suite.

## 3. Expand into features
**Greenfield:** break the goal (the whole app) into **small, vertical, verifiable** features.
**Brownfield:** break down only the **delta** requested in the brief — the requested
change/evolution, not the whole app — respecting the architecture and conventions detected in
step 0. In both cases, each feature must be:
- implementable in isolation within a short session;
- independently testable (there's an unambiguous way to say "it passed");
- given a numeric **priority** (1 = highest); if a feature depends on another (needs something
  the other creates), record that in `dependsOn` — the harness only releases it after its
  dependency(ies) pass, in addition to respecting priority;
- given a **`description`**: an objective description of what the feature does — enough
  context for a future session to understand the scope without rereading the whole brief. Up
  to 700 characters (the harness truncates the excess, but don't rely on that — be objective);
- given **`references`**: the explicit codes the BRIEF cites for this feature (e.g. "RF-003",
  "JIRA-142", a named section) — an empty array if the brief has no explicit code for this
  feature. Don't invent a code that isn't in the brief.

Prefer many small features over a few large ones. A feature you can't verify on its own is too
big — break it down.

## Output
- `$FEATURES`: a JSON ARRAY
  `[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]`
  (just the array; don't include `passes` — every feature starts pending; `dependsOn`/
  `references` empty when there's no dependency/explicit code).
- `$VERIFY_CMD`: the single command that verifies the project (e.g. `dotnet test`, `npm test`).
  Brownfield: reuse the command already established in the project when there is one, instead
  of proposing a new one.
- `$TARGET_DIR`: `app/<descriptive-name>` in greenfield; in brownfield, the actual path where
  the app already lives.

Note: `$VERIFY_CMD` remains the project's canonical command. `verify-feature.sh <id>` is an
operational wrapper for the harness to call without another model turn; initially it can just
run that canonical command for every feature. The wrapper should produce a short verdict
(`PASS`/`FAIL`) and leave detailed logs in the file captured by the harness.
