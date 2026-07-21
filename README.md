# Inverted Agentic Orchestration - a state machine in code, driven by prompt

A harness where **orchestration lives in compiled .NET** and the IDE agent acts
as an **interpreter**: at each step it runs a command, reads `stdout`, and
responds within the expected contract. No model SDK is required, just a
terminal, which makes the same flow portable across Claude Code, GitHub Copilot,
Devin, and Codex.

On this branch, the executable flow is **development**: a long-running flow
that turns a brief into a prioritized list of features and implements one
feature per session until all of them pass.

## What exists on this branch

- Reusable engine in `src/dotnet/Harness.Engine`.
- Development flow in `src/dotnet/Flows.Development`.
- Stable wrapper `run-development.sh`, which runs the Native AOT binary when
  it exists or falls back to the DLL via `dotnet`.
- IDE adapters for Claude Code, GitHub Copilot, Devin, and Codex.
- `package.sh`, which generates a self-contained package with the Native AOT
  binary, skills, and the chosen IDE adapter.
- Deterministic evaluation primitives in the library (`Evaluators.cs`,
  `BatchEvaluator.cs`, `GoldenCaseStore.cs`, `ScoreStore.cs`), but no packaged
  evaluation CLI/flow on this branch.
- A protocol-compatible Python port of the engine and the development flow in
  `src/python` (see "Python port" below) — no .NET SDK required, source-only.

The old `refinement`/`evaluation` flows, the `run-refinement.sh` /
`run-eval.sh` scripts, and the `src/Bench.Eval` CLI are not part of this
branch's current structure.

## Why use this technique

- Flow logic, validation, and state live in **code**, not in the context
  window: the flow is deterministic, testable, and versionable.
- **Portability**: the same `run-*.sh` can be driven by different IDE agents
  without a specific SDK.
- The step ceiling prevents infinite loops and makes spend predictable.
- The state of the run in progress is persisted in `.harness/state.json`, so
  each binary invocation (one per step) doesn't need to carry history in the
  agent's context window. It is live state *for that run* — it survives
  across process invocations, but it is not what decides whether a new
  `start` resets or resumes (see "Resuming across sessions" below; that is
  decided by `.harness/feature_list.json`).
- **`start` is always safe to send**, even when switching agent/driver
  mid-feature: `DevelopmentTasks.Start()` itself decides in code whether to
  reset (no run pending) or resume (a feature is pending) — it does not
  depend on the agent knowing that.
- Native AOT allows shipping a self-contained native binary; anyone who just
  runs the package doesn't need the .NET runtime installed.

This is not a technique for saving tokens by itself. A flow with N steps
generates N interactions with the model. The gain here is operational
control: a governed trajectory, per-step validation, auditable continuity,
and portability.

## When to use it

Use the harness when at least one of these points matters:

- The task needs to follow a fixed step order, validated by code.
- The same flow needs to run across more than one IDE/agent.
- You need to enforce a step ceiling or instruction-size ceiling.
- Process state needs to be auditable (trace + snapshots), and continuity
  across fresh sessions needs to come from artifacts persisted on disk, not
  from the agent's memory.
- The work is naturally incremental, such as implementing a list of small,
  verifiable features.

Prefer a single prompt when the task fits in a direct response, there is no
need for auditing/continuity across sessions, and token cost is the main
priority.

## Development flow

The current flow follows this trajectory:

```text
start -> plan -> [bearings -> smoke -> pick -> implement -> verify -> handoff]*
```

How it works:

- `start` first checks whether a pending feature already exists in
  `.harness/feature_list.json`. **If there is one**, nothing is reset —
  it resumes directly at the `bearings` step of the in-progress feature
  (see "Resuming across sessions" below). **If there isn't one** (the run's
  first `start`, or the previous run already finished with everything
  passing), it starts from scratch: it clears `.harness/state.json` and
  `.harness/trace.jsonl` (the engine's generic layer) and deletes
  `.harness/feature_list.json` and `.harness/run_config.json` (the
  `development` flow's layer), then reads `docs/*.md` / `docs/*.txt` as the
  brief. If `docs/` is empty, it asks the user for the goal, target
  directory, and verification command.
- `plan` writes `.harness/feature_list.json` and `.harness/run_config.json`
  (verification command and target directory).
- `bearings` starts a new feature session and directs the agent to read
  persistent state (`progress.txt`, `git log`, `pwd`).
- `smoke` requires running `./init.sh` in the target directory before any
  change.
- `pick` deterministically selects the highest-priority feature among the
  READY ones (all `dependsOn` already completed).
- `implement` restricts the work to the chosen feature.
- `verify` expects a response with `PASS` or `FAIL: <reason>`; on failure, it
  goes back to `implement` for the same feature.
- `handoff` requires a commit or `NO_GIT: <reason>`, marks the feature as
  complete, and moves to the next one. When all pass, it returns `stop`.

The flow publishes snapshots to `.harness/last-development.trace.jsonl` and
`.harness/last-development.state.json`. Live state lives in
`.harness/state.json`, `.harness/trace.jsonl`, `.harness/feature_list.json`,
and `.harness/run_config.json`.

### Resuming across sessions

There is no `resume` command on this branch — and there doesn't need to be:
**`start` already decides on its own, in code, whether to reset or resume.**
The rule lives in `DevelopmentTasks.Start()`
(`src/dotnet/Flows.Development/DevelopmentTasks.cs`) and is purely mechanical:

- Does `.harness/feature_list.json` have any feature with `passes: false`?
  Then a run is in progress — `start` **resets nothing**, it just returns
  the same instruction `bearings` would return. This covers exactly the case
  of switching agent/driver mid-feature (e.g., Claude runs out of tokens,
  Codex takes over): the new process can send `start` as its first command,
  always, without needing to know a run already existed.
- Otherwise (no feature list, or all already `passes: true`), there is
  nothing to resume — `start` resets `.harness/feature_list.json` and
  `.harness/run_config.json` (`.harness/state.json`/`.harness/trace.jsonl`
  already reset unconditionally on every `start`, see below), and begins a
  genuinely new run.

This works because `verify_cmd`/`target_dir` (written by `plan` into
`.harness/run_config.json`, via `RunConfigStore`) and `feature_list.json`
live **outside** `state.json` — they are not affected by the generic reset
that `TaskRegistry.Dispatch` always performs on `state.json`/`trace.jsonl` on
every `start`. `current_feature_id`, `current_feature_title`, and the
per-feature step ceiling are rewritten from scratch by the following steps
(`pick`, `bearings`) as soon as the loop restarts, so they don't need to
survive the reset.

**Known limit:** resumption happens at the *feature boundary*, not at the
exact position within it. If the session died in the middle of `implement`
or while waiting for `verify`'s verdict, resuming restarts that feature's
session from scratch (`bearings → smoke → pick → implement → verify`) — it
does not recover the exact pending `stdout` (that text is never persisted to
disk). Already-committed work is not lost; uncommitted work must be
recovered by the agent by inspecting `git log`/worktree state during
`bearings`, not blindly repeated.

Content reorientation (what has been done, decisions made) continues to
happen 100% through on-disk artifacts, never the agent's context window:
`progress.txt` in the target directory (the diary the agent itself
maintains) and `git log`/worktree state. That is what the `bearings` step
and the `dev-bearings` skill instruct the agent to do at the start of every
new feature session.

## Structure

```text
src/
  Harness.Engine/             # reusable, domain-agnostic engine
    Envelope.cs               # JSON contract {type,value,args,context}
    TaskRegistry.cs           # dispatch, validation, step ceiling, and timeout
    PromptFormatter.cs        # builds <input>/<response>/<skills>
    StateStore.cs             # persistent state in .harness/state.json
    Trace.cs                  # trace.jsonl and snapshots
    FeatureStore.cs           # feature_list.json for the development flow
    RunConfigStore.cs         # run_config.json (verify_cmd/target_dir), deliberately outside state.json
    Evaluators.cs             # reusable deterministic metrics
    BatchEvaluator.cs         # offline evaluation as a library
    GoldenCaseStore.cs        # loads golden-set cases
    ArtifactStore.cs          # reusable store for other flows' artifacts
    ScoreStore.cs             # reusable store for other flows' scores
    HarnessJsonContext.cs     # JSON source generation for Native AOT
    DocsReader.cs, Inbox.cs, HarnessConfig.cs, PathResolver.cs
  Flows.Development/
    Program.cs                # registers tasks and calls HarnessHost.Run
    DevelopmentTasks.cs       # state machine for the development flow
    DevelopmentTasks.Prompt.cs # prompts emitted by each state
  Harness.Engine.Tests/       # tests for the engine and the development flow
skills/
  dev-*/SKILL.md              # skills injected on demand
run-development.sh            # stable wrapper for Flows.Development
run-checks.sh                 # .NET tests + deterministic flow smoke test
package.sh                    # packages the binary, skills, and IDE adapter
harness.json                  # global harness configuration
```

### Python port (`src/python`)

A second, protocol-compatible implementation of the engine and the development flow,
for environments where only Python (3.11+) is available — no .NET SDK required, no build
step, just an interpreter. It reads/writes the exact same `.harness/*.json(l)` files
(same field names, same paths) as the .NET side, so tooling like
`scripts/harness_cost_correlate.py` works unmodified regardless of which engine produced
the trace. Distributed as source only (`python3 -m`, via `run-development-py.sh`) — no
Native AOT/standalone-binary equivalent and no IDE adapters yet; both are natural
follow-ups, not implemented on this branch.

```text
src/python/
  harness_engine/    # 1:1 port of Harness.Engine (envelope, dispatch, stores, evaluators)
  flows_development/ # 1:1 port of Flows.Development (tasks.py, prompts.py, __main__.py)
  tests/             # pytest, mirrors Harness.Engine.Tests case by case
run-development-py.sh # wrapper: PYTHONPATH=src/python python3 -m flows_development
run-checks-py.sh      # pytest + the same deterministic smoke test, via the Python wrapper
```

## Build and verification

Prerequisite for compiling: a .NET SDK compatible with the projects'
`TargetFramework` (`net10.0`).

DLL build, useful for local development:

```bash
dotnet build src/dotnet/Flows.Development/Flows.Development.csproj -c Release
```

Native AOT, recommended for a self-contained package:

```bash
dotnet publish src/dotnet/Flows.Development/Flows.Development.csproj -c Release -r osx-arm64
```

RIDs used by packaging: `osx-arm64`, `osx-x64`, `linux-x64`,
`linux-arm64`, `win-x64`.

The wrapper first looks for a published native binary at
`src/dotnet/Flows.Development/bin/Release/net10.0/<RID>/publish/`. If not found, it
uses `src/dotnet/Flows.Development/bin/Release/net10.0/Flows.Development.dll` via
`dotnet`, building it on demand on first run if it doesn't exist yet.

```bash
./run-development.sh '{ "type": "text", "value": "start" }'
```

To validate the deterministic layer locally:

```bash
./run-checks.sh
```

This script runs `dotnet test src/dotnet/Harness.Engine.Tests/Harness.Engine.Tests.csproj
-c Release` and an end-to-end smoke test in a temporary directory, using the
inbox transport. It requires no agent and consumes no tokens.

To generate a full package for an IDE:

```bash
./package.sh --os osx-arm64 --ide codex --version 1.0.0
```

Supported IDEs: `claude`, `copilot`, `devin`, `codex`.

Python port, no .NET SDK needed — same protocol, same `.harness/` files:

```bash
./run-development-py.sh '{ "type": "text", "value": "start" }'
./run-checks-py.sh
```

`run-checks-py.sh` runs `pytest src/python/tests` plus the same end-to-end smoke test
(inbox transport, temporary workspace) driven through `run-development-py.sh` instead of
the .NET binary.

## Channel contract

- `stdout` is the **next instruction**: `stop` or an
  `<input>`/`<response>` block.
- `stderr` is **diagnostic** and must not be used to decide the next step.
- The agent's response must be exactly the JSON requested in `<response>`,
  on a single line, with no Markdown fences.

Manual example via argument, useful for understanding the protocol:

```bash
./run-development.sh '{ "type": "text", "value": "start" }'
./run-development.sh '{ "type": "command", "value": "plan", "args": ["[{\"id\":1,\"title\":\"Login\",\"priority\":1}]", "dotnet test", "app"] }'
./run-development.sh '{ "type": "command", "value": "pick" }'
```

Malformed JSON or an unknown command return `ERRO no protocolo` (the
harness's literal error text) on `stdout` and details on `stderr`, without
automatically ending the flow.

## Inbox transport

The harness accepts two transports, in this order:

1. If there is a command-line argument, it uses `args[0]`.
2. If run with no arguments, it reads `.harness/inbox.json`.

IDE adapters always use the inbox: the agent writes the envelope to
`.harness/inbox.json` and runs `./run-development.sh` with no arguments.
This avoids the structural failure of passing single-quoted JSON through the
shell: one forgotten quote hangs the shell before the binary even runs,
preventing the engine from validating the error.

After a successful parse, the inbox is moved to
`.harness/inbox.consumed.json` to leave a debug trail and avoid accidental
reprocessing.

```bash
mkdir -p .harness
printf '%s' '{ "type": "text", "value": "start" }' > .harness/inbox.json
./run-development.sh
```

## Running per IDE

The development flow has one adapter per IDE:

| IDE | Adapter |
|---|---|
| Claude Code | `.claude/agents/development.agent.md` |
| GitHub Copilot | `.github/prompts/development.prompt.md` |
| Devin | `.devin/workflows/development.md` |
| Codex | `.codex/agents/development.toml` |

Expected usage flow:

1. Run the DLL build or publish Native AOT.
2. Put the brief in `docs/` or provide the goal in interactive mode.
3. Ask the IDE agent to use the `development` flow.
4. The agent drives `./run-development.sh` until it receives `stop`.

## Creating a new flow

1. Create `src/dotnet/Flows.<Name>/` and reference `src/dotnet/Harness.Engine`.
2. Implement the domain's state machine and prompts.
3. In `Program.cs`, register a `Dictionary<string, Func<Envelope?, string>>`
   and call `HarnessHost.Run(args, tasks, ...)`.
4. Create a `run-<name>.sh` wrapper pointing to the new flow's binary/DLL.
5. If the flow has IDE adapters or packaging, update `package.sh`.

No orchestration logic needs to be rewritten: dispatch, validation, state,
trace, timeout, inbox, and snapshots all live in `Harness.Engine`.

## Implementation notes

- `Envelope`, `HarnessState`, `TraceEntry`, `RunConfig`, `ScoreReport`, and
  `GoldenCase` use source generation via `HarnessJsonContext`, not
  reflection, to keep Native AOT compatibility.
- `Envelope` is a `record` with custom `Equals`/`GetHashCode`: records
  compare arrays by reference, which would break the value semantics
  expected of `Args`.
- `Envelope` accepts an optional `context`, for example
  `{"driver":"codex"}`. The engine persists that context in the state, and
  `PromptFormatter` automatically reinjects it into subsequent outputs.
- Each flow must publish snapshots at its own path. `development` uses
  `.harness/last-development.*`; the reusable defaults still exist as
  `.harness/last-run.*`.
- The evaluation and artifact files in `Harness.Engine` are library
  components. They remain useful for new flows, but they do not mean this
  branch has a ready-to-use `evaluation` executable.

## Citation

Justino, Y. (2026). Inverted Orchestration in Software Development: A Deterministic Harness and Looping Engineering under Enterprise Constraints (Version v0.1.0). Zenodo. https://doi.org/10.5281/zenodo.21421908

```tex
@misc{justino_2026_21421908,
  author       = {Justino, Yan},
  title        = {Inverted Orchestration in Software Development: A
                   Deterministic Harness and Looping Engineering
                   under Enterprise Constraints
                  },
  month        = jul,
  year         = 2026,
  publisher    = {Zenodo},
  version      = {v0.1.0},
  doi          = {10.5281/zenodo.21421908},
  url          = {https://doi.org/10.5281/zenodo.21421908},
}
```
