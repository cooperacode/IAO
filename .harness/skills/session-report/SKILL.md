---
name: session-report
description: "generate an HTML cost/usage report for the harness's most recent session (tokens, cost per step, per command, errors) for claude, codex, or copilot"
---

# SKILL: session usage and cost report

Generates a self-contained HTML report that correlates the harness steps
(`.harness/trace.jsonl`) with a driver's (IDE/agent) actual token consumption — without this,
"how much did this run cost, step by step" stays stuck in terminal table rows
(`.harness/scripts/harness_cost_correlate.py`), with no consolidated view.

## Running it (the agent's path)

The driver is `.harness/skills/session-report/generate_report.py`. It chains two existing scripts,
without reimplementing anything:

1. `.harness/scripts/<driver>_usage.py --json` — discovers the session that best fits that driver's
   trace for this repo. For Codex, it picks the session with the greatest time overlap and
   uses its subagent tree; for the other drivers, it uses the session with the largest
   `last_ts`.
2. `.harness/scripts/harness_cost_correlate.py --usage-source <driver> --session <id> --trace-file .harness/trace.jsonl --json`
   — correlates the trace steps with that session's consumption. On Codex, the automatic path
   uses `--session-tree <id>` to include all descendants.
3. Normalizes and renders the HTML.

```bash
.harness/skills/session-report/generate_report.py --driver claude
.harness/skills/session-report/generate_report.py --driver codex
.harness/skills/session-report/generate_report.py --driver copilot
```

Generates `report/session-report-<driver>-<timestamp>.html` (folder created if it doesn't
exist) and prints the path at the end. **Confirmed in this session**, running all three
drivers against this repo's real data: using the session that actually generated
`.harness/trace.jsonl` (`--session daba97f0-b838-4b05-92f3-1b778de86d78`), the report
reproduced exactly the numbers from the existing `.harness/report_custo_claude.txt` in the
repo — 57 steps, $10.55 attributed, $11.46 total, 42m 40s duration.

### Scope (optional)

```bash
.harness/skills/session-report/generate_report.py --driver claude --session <session-id>
.harness/skills/session-report/generate_report.py --driver codex --session-tree <session-id>
.harness/skills/session-report/generate_report.py --driver codex --trace-file .harness/last-development.trace.jsonl
.harness/skills/session-report/generate_report.py --driver claude --out-dir /tmp/reports
```

`--session` skips auto-detection and keeps the strict single-session filter.
`--session-tree` (Codex) includes the root and all descendant subagents. Without either, the
Codex report automatically selects the session with the greatest overlap with the trace and
aggregates its tree. `--trace-file` points to another trace (default: `.harness/trace.jsonl`;
the repo also keeps `.harness/last-development.trace.jsonl`, an identical snapshot of the last
run).

## Prerequisites

- Python 3 (tested with 3.12) — no external dependencies, stdlib only.
- `.harness/scripts/claude_usage.py`, `.harness/scripts/codex_usage.py`,
  `.harness/scripts/copilot_usage.py` and `.harness/scripts/harness_cost_correlate.py`
  already present in the harness container.
- A `.harness/trace.jsonl` (or another one passed via `--trace-file`) from a harness run that
  has already happened — without a trace there's no step to correlate.

## What the report shows

- **KPIs**: steps, errors, number of correlated sessions, attributed cost (sum of the steps),
  total scope cost (includes post-last-step consumption, "unattributed"), total tokens,
  average cost/step.
- **Cost per command** — horizontal bar chart + table (colors assigned dynamically to the
  commands seen in the trace, not fixed).
- **Telemetry per command** — duration of the correlated window, token events, tool calls,
  input tokens, cached input tokens, non-cached input tokens, output tokens, reasoning output
  tokens, and average tokens per step. Tool call/event counts depend on the driver's local
  rollout; today it's populated for Codex.
- **Cost per step over the run** — line chart; steps with `outcome: error` show up with a red
  ring.
- **Logged errors** — list of steps with `outcome: error` (hidden if there are none).
- **Tokens and cost per model** — aggregated within the session window (steps + unattributed),
  broken down by input/cache/output/reasoning.
- **Full log** — collapsible table with every step, in original order, including duration,
  token breakdown, and activity counters when available.
- **Warnings** — warnings from the usage/correlate scripts, models without a registered price,
  and the note on Copilot's cost (billed by premium request, not by token — no `$` estimate).

The layout (palette, KPI grid, SVG charts, tooltip) follows the design system of
`curso/material/relatorio-execucao-harness.html` — this report reproduces the cost/execution
section of that template (it's generated from the same data source,
`harness_cost_correlate.py`), but leaves out the features/code-complexity sections, which
depend on `feature_list.json` and Roslyn analysis — out of scope for this skill.

## Gotchas

- **Correlation is by time window, not by a shared key** — Codex now avoids selecting a later
  conversation by preferring the session with the greatest time overlap with the trace and
  includes its subagent tree. If there's no overlap at all, there's still a fallback to the
  session with the largest `last_ts`; in that case, pass
  `--session-tree <id-of-the-root-that-ran-the-harness>` explicitly. Claude and Copilot keep
  selection by `last_ts` and may require `--session`.
- **No trace, no report**: `harness_cost_correlate.py` requires an existing `--trace-file` —
  there's no fallback to "general summary without steps". If `.harness/trace.jsonl` doesn't
  exist (no harness run yet), the driver fails with a message pointing to run the harness
  first.
- **Copilot's cost is always `n/d`** — `harness_cost_correlate.py`'s backend for copilot
  always returns `cost=None` (billed by premium request with a multiplier, not by token); the
  report still shows tokens normally but every cost KPI/column becomes "n/d", with an
  explanatory note in the footer.
- **`--out-dir`/`--trace-file` defaults are always relative to the repo root**, not the
  directory the command was called from — `generate_report.py` resolves `REPO_ROOT` from its
  own file path (`.harness/skills/session-report/generate_report.py` → up three levels).

## Troubleshooting

- `Trace do harness nao encontrado: .harness/trace.jsonl` — run a harness session first
  (`dev-initializer` + feature cycle), or point `--trace-file` to an existing trace (e.g.
  `.harness/last-development.trace.jsonl`).
- `Nenhuma sessao de <driver> encontrada para este repo` — the chosen driver has never been
  used in this repository (the corresponding `<driver>_usage.py` found no sessions). Run the
  usage script directly (`python3 .harness/scripts/claude_usage.py`) to confirm.
- `Erro ao rodar <script>.py (exit 1)` — the underlying script (usage or correlate) failed;
  its stderr is passed through prefixed with `[<script>]` before the final error message.
