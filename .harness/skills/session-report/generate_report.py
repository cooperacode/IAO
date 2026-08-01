#!/usr/bin/env python3
"""Generates an HTML usage/cost report for a driver's most recent session
(claude/codex/copilot), correlating `.harness/trace.jsonl` with the driver's
actual token consumption via scripts/harness_cost_correlate.py. Uses the
layout of curso/material/relatorio-execucao-harness.html as its visual base.

Flow:
    1. scripts/<driver>_usage.py --json  -> discovers the session that best
       fits the trace for this repo (or uses --session/--session-tree, if given).
    2. scripts/harness_cost_correlate.py -> correlates the harness trace steps
       with the token consumption of that session scope.
    3. Renders the HTML in report/.

Usage:
    skills/session-report/generate_report.py --driver claude
    skills/session-report/generate_report.py --driver codex --session <uuid>
    skills/session-report/generate_report.py --driver codex --session-tree <uuid>
    skills/session-report/generate_report.py --driver copilot --trace-file .harness/last-development.trace.jsonl
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SCRIPTS_DIR = REPO_ROOT / "scripts"
DEFAULT_TRACE_FILE = REPO_ROOT / ".harness" / "trace.jsonl"

USAGE_SCRIPT = {
    "claude": SCRIPTS_DIR / "claude_usage.py",
    "codex": SCRIPTS_DIR / "codex_usage.py",
    "copilot": SCRIPTS_DIR / "copilot_usage.py",
}
CORRELATE_SCRIPT = SCRIPTS_DIR / "harness_cost_correlate.py"
DRIVER_LABEL = {
    "claude": "Claude Code",
    "codex": "Codex CLI",
    "copilot": "GitHub Copilot",
}
TOKEN_FIELDS = (
    "input_tokens",
    "cached_input_tokens",
    "cache_write_input_tokens",
    "non_cached_input_tokens",
    "output_tokens",
    "reasoning_output_tokens",
)
ACTIVITY_FIELDS = (
    "token_count_events",
    "tool_calls",
    "tool_outputs",
    "agent_messages",
)


def run_json_script(script: Path, args: list[str], label: str) -> dict:
    cmd = [sys.executable, str(script), *args, "--json"]
    proc = subprocess.run(cmd, capture_output=True, text=True, cwd=REPO_ROOT)
    for line in proc.stderr.splitlines():
        print(f"[{label}] {line}", file=sys.stderr)
    if proc.returncode != 0:
        sys.exit(f"Erro ao rodar {script.name} (exit {proc.returncode})")
    try:
        return json.loads(proc.stdout)
    except json.JSONDecodeError as exc:
        sys.exit(f"Saida de {script.name} nao e JSON valido: {exc}")


def trace_bounds(trace_file: Path) -> tuple[datetime, datetime] | None:
    timestamps: list[datetime] = []
    if not trace_file.is_file():
        return None
    for line in trace_file.read_text().splitlines():
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        timestamp = parse_ts(obj.get("timestamp"))
        if timestamp is not None:
            timestamps.append(timestamp)
    return (min(timestamps), max(timestamps)) if timestamps else None


def find_last_session(driver: str, trace_file: Path | None = None) -> str:
    """Discovers the session that best fits the trace.

    For Codex, prefers the session whose lifetime has the greatest overlap
    with the trace. This avoids selecting a conversation used later just to
    generate the report. For the other drivers, keeps the last_ts-based choice.
    """
    data = run_json_script(USAGE_SCRIPT[driver], [], f"{driver}_usage")
    sessions = data.get("per_session", {})
    if not sessions:
        sys.exit(
            f"Nenhuma sessao de {driver} encontrada para este repo "
            f"(scripts/{driver}_usage.py --json não retornou per_session)."
        )

    def last_ts(sid: str) -> str:
        v = sessions[sid]
        ts = v["totals"]["last_ts"] if driver == "claude" else v.get("last_ts")
        return ts or ""

    bounds = trace_bounds(trace_file) if driver == "codex" and trace_file else None
    if bounds:
        trace_first, trace_last = bounds
        overlapping: list[tuple[float, datetime, str]] = []
        for sid, value in sessions.items():
            first = parse_ts(value.get("first_ts"))
            last = parse_ts(value.get("last_ts"))
            if first is None or last is None:
                continue
            overlap_seconds = (
                min(last, trace_last) - max(first, trace_first)
            ).total_seconds()
            if overlap_seconds >= 0:
                overlapping.append((overlap_seconds, first, sid))
        if overlapping:
            return max(overlapping)[2]

    return max(sessions, key=last_ts)


def run_correlate(
    driver: str,
    session_id: str,
    trace_file: Path,
    session_tree: bool = False,
) -> dict:
    if not trace_file.is_file():
        sys.exit(
            f"Trace do harness nao encontrado: {trace_file}\n"
            "Rode uma sessao do harness (dev-initializer/dev-implement/...) antes de gerar "
            "o relatorio, ou aponte --trace-file para um trace existente "
            "(ex.: .harness/last-development.trace.jsonl)."
        )
    scope_flag = "--session-tree" if session_tree else "--session"
    args = ["--usage-source", driver, scope_flag, session_id, "--trace-file", str(trace_file)]
    return run_json_script(CORRELATE_SCRIPT, args, "harness_cost_correlate")


def run_correlate_by_feature(
    driver: str,
    session_id: str,
    session_tree: bool = False,
) -> dict:
    """Cost per feature via .harness/logs/verify-feature-*.log -- only
    populated for development-flow sessions. Without --trace-file: the
    boundaries come from the verify logs themselves, not from the trace steps.
    Always returns valid JSON (with "features": [] when there are no logs),
    never aborts the report."""
    scope_flag = "--session-tree" if session_tree else "--session"
    args = ["--usage-source", driver, scope_flag, session_id, "--by-feature"]
    return run_json_script(CORRELATE_SCRIPT, args, "harness_cost_correlate_features")


def fmt_usd(v: float | None) -> str:
    return "n/d" if v is None else f"${v:,.2f}"


def fmt_int(v: int) -> str:
    return f"{v:,}".replace(",", ".")


def fmt_tokens(v: int) -> str:
    """Formats an abbreviated token count (10K, 1.5Mi); below 1000 uses fmt_int."""
    abs_v = abs(v)
    if abs_v < 1000:
        return fmt_int(v)
    units = ((1_000_000_000, "Bi"), (1_000_000, "Mi"), (1_000, "K"))
    for i, (div, suffix) in enumerate(units):
        if abs_v < div:
            continue
        # rounding can overflow into the next unit (999999 -> 1000K); bump up
        bump = i > 0 and round(abs_v / div, 1) >= 1000
        d, suf = units[i - 1][:2] if bump else (div, suffix)
        n = f"{v / d:.1f}".rstrip("0").rstrip(".")
        return f"{n}{suf}"
    return fmt_int(v)


def fmt_mmss(seconds: float | None) -> str:
    if seconds is None:
        return "?"
    m, s = divmod(round(seconds), 60)
    return f"{m}m {s:02d}s"


def parse_ts(value: str | None) -> datetime | None:
    if not value:
        return None
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is not None:
        return dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt


def empty_activity() -> dict[str, int]:
    return {field: 0 for field in ACTIVITY_FIELDS}


def usage_breakdown(by_model: dict) -> dict[str, int]:
    totals = {field: 0 for field in TOKEN_FIELDS}
    for mv in by_model.values():
        for field in TOKEN_FIELDS:
            totals[field] += mv.get(field, 0) or 0

    if not totals["non_cached_input_tokens"]:
        totals["non_cached_input_tokens"] = max(
            0,
            totals["input_tokens"]
            - totals["cached_input_tokens"]
            - totals["cache_write_input_tokens"],
        )
    return totals


def find_codex_rollout_paths(
    session_id: str,
    session_tree: bool,
) -> list[Path]:
    scope_flag = "--session-tree" if session_tree else "--session"
    data = run_json_script(
        USAGE_SCRIPT["codex"], [scope_flag, session_id], "codex_usage_activity"
    )
    sessions = data.get("per_session", {})
    paths = []
    for selected in sessions.values():
        path = selected.get("path") if isinstance(selected, dict) else None
        if path:
            paths.append(Path(path).expanduser().resolve(strict=False))
    return paths


def load_step_activity(
    driver: str,
    session_id: str,
    steps: list[dict],
    session_tree: bool = False,
) -> tuple[list[dict[str, int]], list[str]]:
    """Counts the driver's local activity within the same time windows as the correlator.

    Today, tool call/token event counting is supported for Codex, because the
    local JSONL rollout records `response_item.function_call` and
    `event_msg.token_count` with reliable timestamps.
    """
    activity = [empty_activity() for _ in steps]
    notes: list[str] = []
    if driver != "codex" or not steps:
        if driver != "codex":
            notes.append(
                "Telemetria de tool calls/eventos de token por fase atualmente e preenchida apenas para Codex."
            )
        return activity, notes

    rollout_paths = [
        path
        for path in find_codex_rollout_paths(session_id, session_tree)
        if path.is_file()
    ]
    if not rollout_paths:
        notes.append(
            "Rollouts locais do Codex nao encontrados; tool calls/eventos de token ficaram zerados."
        )
        return activity, notes

    step_times = [parse_ts(s.get("timestamp")) for s in steps]
    if any(t is None for t in step_times):
        notes.append(
            "Timestamps do trace invalidos; tool calls/eventos de token ficaram zerados."
        )
        return activity, notes

    for rollout_path in rollout_paths:
        try:
            lines = rollout_path.read_text().splitlines()
        except OSError as exc:
            notes.append(
                f"Nao foi possivel ler o rollout local do Codex ({rollout_path}): {exc}"
            )
            continue

        for line in lines:
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            event_ts = parse_ts(obj.get("timestamp"))
            if event_ts is None:
                continue

            idx = 0
            while idx < len(step_times) and event_ts > step_times[idx]:
                idx += 1
            if idx >= len(step_times):
                continue
            if idx > 0 and event_ts <= step_times[idx - 1]:
                continue

            typ = obj.get("type")
            payload = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}
            payload_type = payload.get("type")
            if typ == "event_msg" and payload_type == "token_count":
                activity[idx]["token_count_events"] += 1
            elif typ == "response_item" and payload_type == "function_call":
                activity[idx]["tool_calls"] += 1
            elif typ == "response_item" and payload_type == "function_call_output":
                activity[idx]["tool_outputs"] += 1
            elif typ == "event_msg" and payload_type == "agent_message":
                activity[idx]["agent_messages"] += 1

    return activity, notes


def build_features(
    driver: str, feature_correlate: dict
) -> tuple[list[dict], dict, list[str]]:
    """Aggregates the harness_cost_correlate.py --by-feature result into rows
    ready for the report. Always returns something (even an empty list) -- it's
    normal for a driver/session to have no feature boundaries (only the
    development flow generates .harness/logs/verify-feature-*.log)."""
    is_copilot = driver == "copilot"
    features_raw = feature_correlate.get("features", [])
    unattributed = feature_correlate.get("unattributed", {})
    warnings = list(feature_correlate.get("warnings", []))

    features = []
    previous_ts: datetime | None = None
    for f in features_raw:
        current_ts = parse_ts(f.get("timestamp"))
        duration_seconds = (
            None
            if current_ts is None or previous_ts is None
            else (current_ts - previous_ts).total_seconds()
        )
        previous_ts = current_ts
        features.append(
            {
                "feature_id": f["feature_id"],
                "title": f["title"],
                "timestamp": f["timestamp"],
                "tokens": f["tokens"],
                "cost": None if is_copilot else f["cost"],
                "duration_seconds": duration_seconds,
                "unpriced_models": f.get("unpriced_models", []),
            }
        )

    tokens_attr = sum(f["tokens"] for f in features)
    cost_attr = None if is_copilot else sum(f["cost"] or 0.0 for f in features)
    tokens_unattr = unattributed.get("tokens", 0) or 0
    cost_unattr = unattributed.get("cost", 0.0) or 0.0

    if features and tokens_unattr:
        warnings.append(
            f"{fmt_tokens(tokens_unattr)} tokens de feature "
            f"({fmt_usd(None if is_copilot else cost_unattr)}) fora de qualquer janela de feature."
        )

    totals = {
        "count": len(features),
        "tokens_attributed": tokens_attr,
        "cost_attributed": cost_attr,
        "tokens_unattributed": tokens_unattr,
        "cost_unattributed": None if is_copilot else cost_unattr,
    }
    return features, totals, warnings


def build_report(
    driver: str,
    session_id: str,
    trace_file: Path,
    correlate: dict,
    feature_correlate: dict,
) -> dict:
    is_copilot = driver == "copilot"
    steps_raw = correlate.get("steps", [])
    unattributed = correlate.get("unattributed", {})
    warnings = list(correlate.get("warnings", []))
    session_scope = correlate.get("session_scope")
    if not isinstance(session_scope, dict):
        session_scope = {
            "type": "session",
            "root": session_id,
            "session_count": 1,
            "session_ids": [session_id],
        }

    features, features_totals, feature_warnings = build_features(
        driver, feature_correlate
    )
    warnings.extend(feature_warnings)

    activity_by_step, activity_notes = load_step_activity(
        driver,
        session_id,
        steps_raw,
        session_tree=session_scope.get("type") == "tree",
    )
    warnings.extend(activity_notes)

    model_totals: dict[str, dict] = defaultdict(
        lambda: {
            "tokens": 0,
            "cost": 0.0,
            **{field: 0 for field in TOKEN_FIELDS},
        }
    )
    unpriced_seen: set[str] = set()

    steps = []
    previous_ts: datetime | None = None
    for i, s in enumerate(steps_raw):
        breakdown = usage_breakdown(s.get("by_model", {}))
        for model, mv in s.get("by_model", {}).items():
            model_totals[model]["tokens"] += mv["total_tokens"]
            model_totals[model]["cost"] += mv.get("cost") or 0.0
            for field in TOKEN_FIELDS:
                model_totals[model][field] += mv.get(field, 0) or 0
        unpriced_seen.update(s.get("unpriced_models", []))

        current_ts = parse_ts(s.get("timestamp"))
        duration_seconds = (
            None
            if current_ts is None or previous_ts is None
            else (current_ts - previous_ts).total_seconds()
        )
        previous_ts = current_ts

        steps.append(
            {
                "step": s["step"],
                "command": s["command"],
                "outcome": s["outcome"],
                "instruction_chars": s["instruction_chars"],
                "timestamp": s["timestamp"],
                "tokens": s["tokens"],
                "cost": s["cost"],
                "duration_seconds": duration_seconds,
                **breakdown,
                **activity_by_step[i],
            }
        )

    for model, mv in unattributed.get("by_model", {}).items():
        model_totals[model]["tokens"] += mv["total_tokens"]
        model_totals[model]["cost"] += mv.get("cost") or 0.0
        for field in TOKEN_FIELDS:
            model_totals[model][field] += mv.get(field, 0) or 0
    unpriced_seen.update(unattributed.get("unpriced_models", []))

    models = [
        {
            "name": name,
            "tokens": v["tokens"],
            "cost": None if is_copilot else v["cost"],
            **{field: v[field] for field in TOKEN_FIELDS},
        }
        for name, v in sorted(model_totals.items(), key=lambda kv: -kv[1]["tokens"])
    ]

    commands_acc: dict[str, dict] = defaultdict(
        lambda: {
            "cost": 0.0,
            "tokens": 0,
            "steps": 0,
            "errors": 0,
            "duration_seconds": 0.0,
            **{field: 0 for field in TOKEN_FIELDS},
            **{field: 0 for field in ACTIVITY_FIELDS},
        }
    )
    for s in steps:
        c = commands_acc[s["command"]]
        c["cost"] += s["cost"]
        c["tokens"] += s["tokens"]
        c["steps"] += 1
        c["duration_seconds"] += s["duration_seconds"] or 0.0
        for field in TOKEN_FIELDS:
            c[field] += s[field]
        for field in ACTIVITY_FIELDS:
            c[field] += s[field]
        if s["outcome"] == "error":
            c["errors"] += 1
    commands = sorted(
        ({"cmd": k, **v} for k, v in commands_acc.items()),
        key=lambda c: (-c["cost"], -c["tokens"]),
    )

    errors = [s for s in steps if s["outcome"] == "error"]

    tokens_attributed = sum(s["tokens"] for s in steps)
    cost_attributed = sum(s["cost"] for s in steps)
    tokens_unattr = unattributed.get("tokens", 0) or 0
    cost_unattr = unattributed.get("cost", 0.0) or 0.0

    first_ts = steps[0]["timestamp"] if steps else None
    last_ts = steps[-1]["timestamp"] if steps else None
    duration_seconds = None
    if first_ts and last_ts:
        duration_seconds = (parse_ts(last_ts) - parse_ts(first_ts)).total_seconds()

    notes = warnings
    if unpriced_seen and not is_copilot:
        notes.append("Sem preco cadastrado para: " + ", ".join(sorted(unpriced_seen)))
    if is_copilot:
        notes.append(
            "Copilot fatura por premium request com multiplicador, nao por token -- "
            "sem estimativa de custo em dolar (tokens sao aproximados)."
        )
    if tokens_unattr:
        notes.append(
            f"{fmt_tokens(tokens_unattr)} tokens "
            f"({fmt_usd(None if is_copilot else cost_unattr)}) registrados apos o ultimo "
            "passo do trace, nao atribuidos a nenhum passo."
        )

    pricing = correlate.get("pricing")

    return {
        "driver": driver,
        "driver_label": DRIVER_LABEL[driver],
        "session_id": session_id,
        "session_scope": session_scope,
        "trace_file": str(trace_file.relative_to(REPO_ROOT))
        if trace_file.is_relative_to(REPO_ROOT)
        else str(trace_file),
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC"),
        "pricing": pricing if isinstance(pricing, dict) else None,
        "models": models,
        "commands": commands,
        "steps": steps,
        "errors": errors,
        "features": features,
        "features_totals": features_totals,
        "notes": notes,
        "totals": {
            "steps": len(steps),
            "errors": len(errors),
            "tokens_attributed": tokens_attributed,
            "cost_attributed": None if is_copilot else cost_attributed,
            "tokens_unattributed": tokens_unattr,
            "cost_unattributed": None if is_copilot else cost_unattr,
            "tokens_total": tokens_attributed + tokens_unattr,
            "cost_total": None if is_copilot else (cost_attributed + cost_unattr),
            "avg_cost_step": None
            if is_copilot or not steps
            else cost_attributed / len(steps),
            "duration_seconds": duration_seconds,
            "first_ts": first_ts,
            "last_ts": last_ts,
            "token_count_events": sum(s["token_count_events"] for s in steps),
            "tool_calls": sum(s["tool_calls"] for s in steps),
            "tool_outputs": sum(s["tool_outputs"] for s in steps),
            "agent_messages": sum(s["agent_messages"] for s in steps),
        },
    }


HTML_TEMPLATE = r"""<!DOCTYPE html>
<html lang="pt-BR">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>__TITLE__</title>
<style>
  :root{
    color-scheme: light;
    --page:      #f7f8f7;
    --surface-1: #fcfcfb;
    --surface-2: #f0f1f0;
    --text-primary:   #0b0b0b;
    --text-secondary: #52514e;
    --text-muted:     #898781;
    --grid:      #e1e0d9;
    --baseline:  #c3c2b7;
    --border:    rgba(11,11,11,0.10);
    --good:      #0ca30c;
    --critical:  #d03b3b;
    --accent:    #2a78d6;
    --accent-soft: rgba(42,120,214,0.12);
    --s1: #2a78d6; --s2: #008300; --s3: #e87ba4; --s4: #eda100;
    --s5: #1baf7a; --s6: #eb6834; --s7: #4a3aa7; --s8: #e34948;
    --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
    --sans: system-ui, -apple-system, "Segoe UI", sans-serif;
  }
  @media (prefers-color-scheme: dark) {
    :root:where(:not([data-theme="light"])) {
      color-scheme: dark;
      --page:      #0d0d0d;
      --surface-1: #17181a;
      --surface-2: #1e2022;
      --text-primary:   #ffffff;
      --text-secondary: #c3c2b7;
      --text-muted:     #8b8a85;
      --grid:      #2c2c2a;
      --baseline:  #3a3a38;
      --border:    rgba(255,255,255,0.10);
      --good:      #0ca30c;
      --critical:  #e66767;
      --accent:    #3987e5;
      --accent-soft: rgba(57,135,229,0.16);
      --s1: #3987e5; --s2: #008300; --s3: #d55181; --s4: #c98500;
      --s5: #199e70; --s6: #d95926; --s7: #9085e9; --s8: #e66767;
    }
  }
  :root[data-theme="dark"] {
    color-scheme: dark;
    --page:      #0d0d0d; --surface-1: #17181a; --surface-2: #1e2022;
    --text-primary:   #ffffff; --text-secondary: #c3c2b7; --text-muted:     #8b8a85;
    --grid:      #2c2c2a; --baseline:  #3a3a38; --border:    rgba(255,255,255,0.10);
    --good:      #0ca30c; --critical:  #e66767; --accent:    #3987e5; --accent-soft: rgba(57,135,229,0.16);
    --s1: #3987e5; --s2: #008300; --s3: #d55181; --s4: #c98500;
    --s5: #199e70; --s6: #d95926; --s7: #9085e9; --s8: #e66767;
  }
  :root[data-theme="light"] {
    color-scheme: light;
    --page: #f7f8f7; --surface-1:#fcfcfb; --surface-2:#f0f1f0;
    --text-primary:#0b0b0b; --text-secondary:#52514e; --text-muted:#898781;
    --grid:#e1e0d9; --baseline:#c3c2b7; --border:rgba(11,11,11,0.10);
    --good:#0ca30c; --critical:#d03b3b; --accent:#2a78d6; --accent-soft: rgba(42,120,214,0.12);
    --s1:#2a78d6; --s2:#008300; --s3:#e87ba4; --s4:#eda100; --s5:#1baf7a; --s6:#eb6834; --s7:#4a3aa7; --s8:#e34948;
  }
  *{ box-sizing:border-box; }
  html,body{ margin:0; padding:0; }
  body{ background:var(--page); color:var(--text-primary); font-family:var(--sans); line-height:1.5; -webkit-font-smoothing:antialiased; }
  ::selection{ background:var(--accent-soft); }
  .wrap{ max-width:1080px; margin:0 auto; padding:40px 24px 80px; }
  header.top{ margin-bottom:36px; }
  .eyebrow{ font-family:var(--mono); font-size:12px; letter-spacing:.08em; text-transform:uppercase; color:var(--accent); margin:0 0 10px; }
  h1{ font-size:clamp(24px,4vw,34px); margin:0 0 8px; text-wrap:balance; letter-spacing:-0.01em; }
  .sub{ color:var(--text-secondary); font-size:15px; max-width:70ch; margin:0 0 18px; }
  .meta-row{ display:flex; flex-wrap:wrap; gap:8px 10px; }
  .meta-chip{ font-family:var(--mono); font-size:12px; color:var(--text-secondary); background:var(--surface-2); border:1px solid var(--border); border-radius:6px; padding:4px 9px; white-space:nowrap; }
  .meta-chip b{ color:var(--text-primary); font-weight:600; }
  section{ margin-top:44px; }
  .section-head{ display:flex; align-items:baseline; justify-content:space-between; gap:16px; margin-bottom:16px; flex-wrap:wrap; }
  h2{ font-size:17px; margin:0; letter-spacing:-0.01em; }
  .section-note{ font-size:12.5px; color:var(--text-muted); margin:0; max-width:60ch; }
  .card{ background:var(--surface-1); border:1px solid var(--border); border-radius:12px; padding:20px 22px; }
  .kpis{ display:grid; grid-template-columns:repeat(6,1fr); gap:1px; background:var(--border); border:1px solid var(--border); border-radius:12px; overflow:hidden; }
  .kpi{ background:var(--surface-1); padding:16px 14px; min-width:0; }
  .kpi .label{ font-size:11px; text-transform:uppercase; letter-spacing:.06em; color:var(--text-muted); margin-bottom:8px; }
  .kpi .value{ font-family:var(--mono); font-variant-numeric:tabular-nums; font-size:22px; font-weight:600; letter-spacing:-0.02em; }
  .kpi .value.good{ color:var(--good); }
  .kpi .value.crit{ color:var(--critical); }
  .kpi .sub{ font-size:11.5px; color:var(--text-muted); margin-top:4px; }
  @media (max-width:860px){ .kpis{ grid-template-columns:repeat(3,1fr); } }
  @media (max-width:480px){ .kpis{ grid-template-columns:repeat(2,1fr); } }
  svg{ display:block; overflow:visible; font-family:var(--mono); }
  .axis-label{ fill:var(--text-muted); font-size:10.5px; }
  .grid-line{ stroke:var(--grid); stroke-width:1; }
  .baseline{ stroke:var(--baseline); stroke-width:1; }
  .bar-value{ fill:var(--text-primary); font-size:11.5px; font-weight:600; font-variant-numeric:tabular-nums; }
  .bar-sub{ fill:var(--text-muted); font-size:10px; }
  .hit{ fill:transparent; cursor:pointer; }
  .hit:hover + .bar-rect, .bar-rect.hovered{ filter:brightness(1.08); }
  .bar-rect{ transition:filter .1s ease; }
  .tooltip{ position:fixed; pointer-events:none; z-index:50; display:none; background:var(--text-primary); color:var(--page); font-family:var(--mono); font-size:12px; line-height:1.5; padding:8px 11px; border-radius:8px; max-width:280px; box-shadow:0 8px 24px rgba(0,0,0,0.25); }
  .tooltip b{ display:block; font-family:var(--sans); font-size:12.5px; margin-bottom:2px; }
  .tooltip .tt-row{ display:flex; justify-content:space-between; gap:14px; opacity:.92; }
  .legend-row{ display:flex; flex-wrap:wrap; gap:14px; margin-top:14px; }
  .legend-item{ display:flex; align-items:center; gap:6px; font-size:12px; color:var(--text-secondary); }
  .legend-swatch{ width:10px; height:10px; border-radius:2px; flex:none; }
  .legend-mark{ width:10px; height:10px; border-radius:50%; border:2px solid var(--critical); background:none; flex:none; }
  .table-scroll{ overflow-x:auto; border:1px solid var(--border); border-radius:12px; }
  table{ border-collapse:collapse; width:100%; min-width:640px; font-size:13px; }
  table.wide{ min-width:1040px; }
  table.xwide{ min-width:1240px; }
  thead th{ position:sticky; top:0; background:var(--surface-2); text-align:left; font-weight:600; font-size:11px; text-transform:uppercase; letter-spacing:.04em; color:var(--text-muted); padding:10px 14px; border-bottom:1px solid var(--border); white-space:nowrap; }
  tbody td{ padding:10px 14px; border-bottom:1px solid var(--border); vertical-align:top; color:var(--text-secondary); }
  tbody tr:last-child td{ border-bottom:none; }
  tbody tr:hover td{ background:var(--surface-2); }
  td.num, th.num{ text-align:right; font-family:var(--mono); font-variant-numeric:tabular-nums; color:var(--text-primary); }
  .mono-cell{ font-family:var(--mono); color:var(--text-primary); }
  .pill{ display:inline-flex; align-items:center; gap:5px; font-size:11.5px; font-weight:600; padding:3px 9px; border-radius:100px; }
  .pill.ok{ background:rgba(12,163,12,0.13); color:var(--good); }
  .pill.err{ background:rgba(208,59,59,0.13); color:var(--critical); }
  .pill::before{ content:""; width:6px; height:6px; border-radius:50%; background:currentColor; }
  .cmd-tag{ font-family:var(--mono); font-size:12px; padding:2px 7px; border-radius:5px; color:#fff; }
  details{ margin-top:8px; }
  summary{ cursor:pointer; font-size:13px; color:var(--accent); font-weight:600; list-style:none; display:inline-flex; align-items:center; gap:6px; padding:4px 0; }
  summary::-webkit-details-marker{ display:none; }
  summary::before{ content:"▸"; font-size:11px; transition:transform .15s ease; }
  details[open] summary::before{ transform:rotate(90deg); }
  .notes-list{ display:flex; flex-direction:column; gap:8px; margin:0; padding-left:18px; font-size:13px; color:var(--text-secondary); }
  .errors-list{ display:flex; flex-direction:column; gap:0; margin-top:0; }
  .error-row{ display:grid; grid-template-columns:70px 100px 1fr 90px; gap:14px; align-items:center; padding:10px 4px; border-bottom:1px solid var(--border); font-size:12.5px; }
  .error-row:last-child{ border-bottom:none; }
  .error-row .step{ font-family:var(--mono); color:var(--text-muted); }
  .error-row .cost{ font-family:var(--mono); font-variant-numeric:tabular-nums; text-align:right; color:var(--critical); font-weight:600; }
  footer{ margin-top:56px; padding-top:20px; border-top:1px solid var(--border); font-size:12px; color:var(--text-muted); }
  footer p{ margin:0 0 6px; max-width:80ch; }
  code{ font-family:var(--mono); background:var(--surface-2); padding:1px 5px; border-radius:4px; font-size:11.5px; }
</style>
</head>
<body>
<div class="wrap">
  <header class="top">
    <p class="eyebrow">Relatorio de uso e custo · Sessao do harness</p>
    <h1>__H1__</h1>
    <p class="sub">Passos de <code>__TRACE_FILE__</code> correlacionados com o consumo real de tokens do escopo <code>__SESSION_SHORT__</code> (__SESSION_COUNT_LABEL__) via
    <code>scripts/harness_cost_correlate.py</code>. Driver: __DRIVER_LABEL__.</p>
    <div class="meta-row">
      <span class="meta-chip">__SESSION_SCOPE_LABEL__ <b>__SESSION_SHORT__</b></span>
      <span class="meta-chip">sessoes <b>__SESSION_COUNT__</b></span>
      <span class="meta-chip">primeiro passo <b>__FIRST_TS__</b></span>
      <span class="meta-chip">ultimo passo <b>__LAST_TS__</b></span>
      <span class="meta-chip">duracao <b>__DURATION__</b></span>
      <span class="meta-chip">gerado em <b>__GENERATED_AT__</b></span>
    </div>
  </header>

  <section aria-label="Indicadores gerais">
    <div class="kpis">
      <div class="kpi">
        <div class="label">Passos</div>
        <div class="value">__KPI_STEPS__</div>
      </div>
      <div class="kpi">
        <div class="label">Erros</div>
        <div class="value __KPI_ERRORS_CLASS__">__KPI_ERRORS__</div>
      </div>
      <div class="kpi">
        <div class="label">Custo atribuido</div>
        <div class="value">__KPI_COST_ATTR__</div>
        <div class="sub">soma dos passos</div>
      </div>
      <div class="kpi">
        <div class="label">Custo total</div>
        <div class="value">__KPI_COST_TOTAL__</div>
        <div class="sub">__KPI_COST_TOTAL_SUB__</div>
      </div>
      <div class="kpi">
        <div class="label">Tokens totais</div>
        <div class="value">__KPI_TOKENS__</div>
      </div>
      <div class="kpi">
        <div class="label">Custo medio / passo</div>
        <div class="value">__KPI_AVG__</div>
      </div>
    </div>
  </section>

  <section>
    <div class="section-head">
      <h2>Custo por tipo de comando</h2>
      <p class="section-note">Comandos do ciclo do harness observados no trace, ordenados por custo total.</p>
    </div>
    <div class="card">
      <div id="chart-commands"></div>
    </div>
    <div class="table-scroll" style="margin-top:16px;">
      <table>
        <thead><tr><th>Comando</th><th class="num">Passos</th><th class="num">Erros</th><th class="num">Tokens</th><th class="num">Custo</th></tr></thead>
        <tbody id="tbl-commands"></tbody>
      </table>
    </div>
  </section>

  <section>
    <div class="section-head">
      <h2>Custo por feature</h2>
      <p class="section-note">Custo atribuido a cada feature via <code>.harness/logs/verify-feature-*.log</code> (timestamp de verify de cada feature, nao contagem de passos) -- so disponivel para sessoes do fluxo development.</p>
    </div>
    <div class="card">
      <div id="chart-features"></div>
    </div>
    <div class="table-scroll" style="margin-top:16px;">
      <table>
        <thead><tr><th>Feature</th><th>Titulo</th><th class="num">Duracao</th><th class="num">Tokens</th><th class="num">Custo</th></tr></thead>
        <tbody id="tbl-features"></tbody>
      </table>
    </div>
  </section>

  <section>
    <div class="section-head">
      <h2>Telemetria por comando</h2>
      <p class="section-note">Quebra de tokens e atividade do driver nas mesmas janelas de tempo usadas para atribuir custo. Tool calls/eventos sao preenchidos quando o rollout local do driver expoe esses eventos.</p>
    </div>
    <div class="table-scroll">
      <table class="xwide">
        <thead>
          <tr>
            <th>Comando</th>
            <th class="num">Duracao</th>
            <th class="num">Eventos token</th>
            <th class="num">Tool calls</th>
            <th class="num">Input</th>
            <th class="num">Cache</th>
            <th class="num">Nao cache</th>
            <th class="num">Output</th>
            <th class="num">Raciocinio</th>
            <th class="num">Tokens / passo</th>
          </tr>
        </thead>
        <tbody id="tbl-telemetry"></tbody>
      </table>
    </div>
  </section>

  <section>
    <div class="section-head">
      <h2>Custo por passo ao longo da execucao</h2>
      <p class="section-note">Cada ponto e um passo do trace. Os aneis vermelhos marcam passos com outcome = error.</p>
    </div>
    <div class="card">
      <div id="chart-timeline"></div>
      <div class="legend-row">
        <span class="legend-item"><span class="legend-swatch" style="background:var(--s1)"></span>custo do passo (USD)</span>
        <span class="legend-item"><span class="legend-mark"></span>passo com outcome = error</span>
      </div>
    </div>
  </section>

  <section id="errors-section" style="display:none;">
    <div class="section-head">
      <h2>Erros registrados</h2>
      <p class="section-note" id="errors-note"></p>
    </div>
    <div class="card"><div class="errors-list" id="errors-list"></div></div>
  </section>

  <section>
    <div class="section-head">
      <h2>Tokens e custo por modelo</h2>
      <p class="section-note">Consumo agregado por modelo dentro da janela do escopo de sessoes (passos + nao atribuido).</p>
    </div>
    <div class="table-scroll">
      <table class="wide">
        <thead>
          <tr>
            <th>Modelo</th>
            <th class="num">Tokens</th>
            <th class="num">Input</th>
            <th class="num">Cache</th>
            <th class="num">Nao cache</th>
            <th class="num">Output</th>
            <th class="num">Raciocinio</th>
            <th class="num">Custo</th>
          </tr>
        </thead>
        <tbody id="tbl-models"></tbody>
      </table>
    </div>
  </section>

  <section>
    <div class="section-head">
      <h2>Log completo de execucao</h2>
      <p class="section-note">Os __KPI_STEPS__ passos do trace, na ordem original.</p>
    </div>
    <details>
      <summary>Expandir os passos</summary>
      <div class="table-scroll" style="margin-top:12px; max-height:520px; overflow-y:auto;">
        <table class="xwide">
          <thead>
            <tr>
              <th class="num">Passo</th>
              <th>Comando</th>
              <th>Outcome</th>
              <th class="num">Duracao</th>
              <th class="num">Chars instrucao</th>
              <th class="num">Tokens</th>
              <th class="num">Input</th>
              <th class="num">Cache</th>
              <th class="num">Output</th>
              <th class="num">Eventos token</th>
              <th class="num">Tool calls</th>
              <th class="num">Custo</th>
            </tr>
          </thead>
          <tbody id="tbl-log"></tbody>
        </table>
      </div>
    </details>
  </section>

  <section id="notes-section" style="display:none;">
    <div class="section-head"><h2>Avisos</h2></div>
    <div class="card"><ul class="notes-list" id="notes-list"></ul></div>
  </section>

  <footer>
    <p><b>Fontes:</b> <code>__TRACE_FILE__</code> (__KPI_STEPS__ passos) correlacionado via <code>scripts/harness_cost_correlate.py --usage-source __DRIVER__ __SESSION_FLAG__ __SESSION_SHORT__</code>, contra o escopo de __SESSION_COUNT_LABEL__ de __DRIVER_LABEL__ para este repo.</p>
    <p>Custos sao estimativas com base na tabela de precos publica embutida nos scripts; passos com modelo sem preco cadastrado entram nos tokens mas nao no custo.</p>
  </footer>
</div>

<div class="tooltip" id="tooltip"></div>

<script>
(function(){
  const $ = (sel, root=document) => root.querySelector(sel);
  const fmtUSD = v => v == null ? 'n/d' : ('$' + v.toFixed(4));
  const fmtUSD2 = v => v == null ? 'n/d' : ('$' + v.toFixed(2));
  const fmtInt = v => v.toLocaleString('pt-BR');
  const fmtTok = v => {
    const abs = Math.abs(v);
    if (abs < 1000) return fmtInt(v);
    const units = [[1e9,'Bi'],[1e6,'Mi'],[1e3,'K']];
    for (let i = 0; i < units.length; i++){
      const [div, suffix] = units[i];
      if (abs < div) continue;
      // rounding can overflow into the next unit (999999 -> 1000K); bump up
      const bump = i > 0 && Math.round(abs / div * 10) / 10 >= 1000;
      const [d, suf] = bump ? units[i - 1] : [div, suffix];
      const n = (v / d).toFixed(1).replace(/\.0$/, '');
      return n + suf;
    }
    return fmtInt(v);
  };
  const fmtDuration = v => {
    if (v == null) return 'n/d';
    const total = Math.max(0, Math.round(v));
    const m = Math.floor(total / 60);
    const s = total % 60;
    return m ? `${m}m ${String(s).padStart(2, '0')}s` : `${s}s`;
  };

  const tooltip = $('#tooltip');
  function showTip(evt, html){
    tooltip.innerHTML = html;
    tooltip.style.display = 'block';
    const pad = 14;
    tooltip.style.left = (evt.clientX + pad) + 'px';
    tooltip.style.top = (evt.clientY + pad) + 'px';
  }
  function hideTip(){ tooltip.style.display = 'none'; }

  const commands = __COMMANDS_JSON__;
  const steps = __STEPS_JSON__;
  const errors = __ERRORS_JSON__;
  const models = __MODELS_JSON__;
  const features = __FEATURES_JSON__;
  const notes = __NOTES_JSON__;

  const palette = ['--s1','--s2','--s3','--s4','--s5','--s6','--s7','--s8'];
  const cmdColor = {};
  commands.forEach((c,i) => { cmdColor[c.cmd] = palette[i % palette.length]; });
  const colorOf = cmd => `var(${cmdColor[cmd] || '--s1'})`;
  const featColorOf = i => `var(${palette[i % palette.length]})`;

  // ---------- notes ----------
  if (notes.length){
    $('#notes-section').style.display = '';
    $('#notes-list').innerHTML = notes.map(n => `<li>${n}</li>`).join('');
  }

  // ---------- table: commands ----------
  $('#tbl-commands').innerHTML = commands.map(c => `
    <tr>
      <td><span class="cmd-tag" style="background:${colorOf(c.cmd)}">${c.cmd}</span></td>
      <td class="num">${c.steps}</td>
      <td class="num">${c.errors || '—'}</td>
      <td class="num">${fmtTok(c.tokens)}</td>
      <td class="num">${fmtUSD(c.cost)}</td>
    </tr>`).join('');

  // ---------- table: features ----------
  $('#tbl-features').innerHTML = features.length ? features.map((f,i) => `
    <tr>
      <td><span class="cmd-tag" style="background:${featColorOf(i)}">#${f.feature_id}</span></td>
      <td>${f.title}</td>
      <td class="num">${fmtDuration(f.duration_seconds)}</td>
      <td class="num">${fmtTok(f.tokens)}</td>
      <td class="num">${fmtUSD(f.cost)}</td>
    </tr>`).join('') : `<tr><td colspan="5" style="color:var(--text-muted); text-align:center;">Sem dados de feature para esta sessao (o fluxo nao gerou verify-feature-*.log).</td></tr>`;

  // ---------- table: command telemetry ----------
  $('#tbl-telemetry').innerHTML = commands.map(c => `
    <tr>
      <td><span class="cmd-tag" style="background:${colorOf(c.cmd)}">${c.cmd}</span></td>
      <td class="num">${fmtDuration(c.duration_seconds)}</td>
      <td class="num">${fmtInt(c.token_count_events || 0)}</td>
      <td class="num">${fmtInt(c.tool_calls || 0)}</td>
      <td class="num">${fmtTok(c.input_tokens || 0)}</td>
      <td class="num">${fmtTok(c.cached_input_tokens || 0)}</td>
      <td class="num">${fmtTok(c.non_cached_input_tokens || 0)}</td>
      <td class="num">${fmtTok(c.output_tokens || 0)}</td>
      <td class="num">${fmtTok(c.reasoning_output_tokens || 0)}</td>
      <td class="num">${fmtTok(Math.round((c.tokens || 0) / Math.max(1, c.steps || 1)))}</td>
    </tr>`).join('');

  // ---------- table: models ----------
  $('#tbl-models').innerHTML = models.map(m => `
    <tr>
      <td class="mono-cell">${m.name}</td>
      <td class="num">${fmtTok(m.tokens)}</td>
      <td class="num">${fmtTok(m.input_tokens || 0)}</td>
      <td class="num">${fmtTok(m.cached_input_tokens || 0)}</td>
      <td class="num">${fmtTok(m.non_cached_input_tokens || 0)}</td>
      <td class="num">${fmtTok(m.output_tokens || 0)}</td>
      <td class="num">${fmtTok(m.reasoning_output_tokens || 0)}</td>
      <td class="num">${fmtUSD(m.cost)}</td>
    </tr>`).join('');

  // ---------- table: full log ----------
  $('#tbl-log').innerHTML = steps.map(s => {
    const pill = s.outcome === 'error' ? '<span class="pill err">error</span>' : '<span class="pill ok">' + s.outcome + '</span>';
    return `<tr>
      <td class="num mono-cell">${s.step}</td>
      <td><span class="cmd-tag" style="background:${colorOf(s.command)}">${s.command}</span></td>
      <td>${pill}</td>
      <td class="num">${fmtDuration(s.duration_seconds)}</td>
      <td class="num">${fmtInt(s.instruction_chars)}</td>
      <td class="num">${fmtTok(s.tokens)}</td>
      <td class="num">${fmtTok(s.input_tokens || 0)}</td>
      <td class="num">${fmtTok(s.cached_input_tokens || 0)}</td>
      <td class="num">${fmtTok(s.output_tokens || 0)}</td>
      <td class="num">${fmtInt(s.token_count_events || 0)}</td>
      <td class="num">${fmtInt(s.tool_calls || 0)}</td>
      <td class="num">${fmtUSD(s.cost)}</td>
    </tr>`;
  }).join('');

  // ---------- errors list ----------
  if (errors.length){
    $('#errors-section').style.display = '';
    $('#errors-note').textContent = errors.length + ' erro(s) no trace, cada um seguido do proximo passo.';
    $('#errors-list').innerHTML = errors.map(s => `<div class="error-row">
      <span class="step">passo ${s.step}</span>
      <span class="cmd-tag" style="background:${colorOf(s.command)}">${s.command}</span>
      <span>${fmtTok(s.tokens)} tokens</span>
      <span class="cost">${fmtUSD(s.cost)}</span>
    </div>`).join('');
  }

  // ================= chart: cost per command (horizontal bar) =================
  (function(){
    const el = $('#chart-commands');
    if (!commands.length){ el.innerHTML = '<p style="color:var(--text-muted); font-size:13px;">sem dados</p>'; return; }
    const W = el.clientWidth || 1000;
    const rowH = 34, gap = 10;
    const M = {top:6, right:70, bottom:6, left:110};
    const n = commands.length;
    const H = M.top + M.bottom + n*rowH + (n-1)*gap;
    const plotW = W - M.left - M.right;
    const maxV = Math.max(...commands.map(c => c.cost), 0.0001) * 1.08;
    const totalAll = commands.reduce((a,c)=>a+c.cost,0);

    let svg = `<svg viewBox="0 0 ${W} ${H}" width="100%" height="${H}">`;
    commands.forEach((c,i) => {
      const y = M.top + i*(rowH+gap);
      const w = (c.cost/maxV) * plotW;
      svg += `<text class="axis-label" x="${M.left-10}" y="${y+rowH/2+4}" text-anchor="end" font-family="var(--mono)">${c.cmd}</text>`;
      svg += `<rect class="bar-rect" x="${M.left}" y="${y}" width="${w}" height="${rowH-10}" rx="4" fill="${colorOf(c.cmd)}"/>`;
      svg += `<text class="bar-value" x="${M.left+w+10}" y="${y+rowH/2-4}">${fmtUSD2(c.cost)}</text>`;
      svg += `<text class="bar-sub" x="${M.left+w+10}" y="${y+rowH/2+10}">${totalAll ? ((c.cost/totalAll)*100).toFixed(1) : '0.0'}%</text>`;
      svg += `<rect class="hit" data-i="${i}" x="0" y="${y-gap/2}" width="${W}" height="${rowH+gap}"/>`;
    });
    svg += `</svg>`;
    el.innerHTML = svg;
    el.querySelectorAll('.hit').forEach(hit => {
      const i = +hit.dataset.i, c = commands[i];
      hit.addEventListener('mousemove', e => showTip(e, `<b>${c.cmd}</b>
        <div class="tt-row"><span>custo total</span><span>${fmtUSD(c.cost)}</span></div>
        <div class="tt-row"><span>tokens</span><span>${fmtTok(c.tokens)}</span></div>
        <div class="tt-row"><span>tool calls</span><span>${fmtInt(c.tool_calls || 0)}</span></div>
        <div class="tt-row"><span>eventos token</span><span>${fmtInt(c.token_count_events || 0)}</span></div>
        <div class="tt-row"><span>passos</span><span>${c.steps}</span></div>
        <div class="tt-row"><span>erros</span><span>${c.errors}</span></div>
        <div class="tt-row"><span>share do custo</span><span>${totalAll ? ((c.cost/totalAll)*100).toFixed(1) : '0.0'}%</span></div>`));
      hit.addEventListener('mouseleave', hideTip);
    });
  })();

  // ================= chart: cost per feature (horizontal bar) =================
  (function(){
    const el = $('#chart-features');
    if (!features.length){ el.innerHTML = '<p style="color:var(--text-muted); font-size:13px;">sem dados de feature para esta sessao</p>'; return; }
    const W = el.clientWidth || 1000;
    const rowH = 34, gap = 10;
    const M = {top:6, right:70, bottom:6, left:44};
    const n = features.length;
    const H = M.top + M.bottom + n*rowH + (n-1)*gap;
    const plotW = W - M.left - M.right;
    const maxV = Math.max(...features.map(f => f.cost || 0), 0.0001) * 1.08;
    const totalAll = features.reduce((a,f)=>a+(f.cost || 0),0);

    let svg = `<svg viewBox="0 0 ${W} ${H}" width="100%" height="${H}">`;
    features.forEach((f,i) => {
      const y = M.top + i*(rowH+gap);
      const w = ((f.cost || 0)/maxV) * plotW;
      svg += `<text class="axis-label" x="${M.left-10}" y="${y+rowH/2+4}" text-anchor="end" font-family="var(--mono)">#${f.feature_id}</text>`;
      svg += `<rect class="bar-rect" x="${M.left}" y="${y}" width="${w}" height="${rowH-10}" rx="4" fill="${featColorOf(i)}"/>`;
      svg += `<text class="bar-value" x="${M.left+w+10}" y="${y+rowH/2-4}">${fmtUSD2(f.cost)}</text>`;
      svg += `<text class="bar-sub" x="${M.left+w+10}" y="${y+rowH/2+10}">${totalAll ? (((f.cost||0)/totalAll)*100).toFixed(1) : '0.0'}%</text>`;
      svg += `<rect class="hit" data-i="${i}" x="0" y="${y-gap/2}" width="${W}" height="${rowH+gap}"/>`;
    });
    svg += `</svg>`;
    el.innerHTML = svg;
    el.querySelectorAll('.hit').forEach(hit => {
      const i = +hit.dataset.i, f = features[i];
      hit.addEventListener('mousemove', e => showTip(e, `<b>#${f.feature_id} · ${f.title}</b>
        <div class="tt-row"><span>custo</span><span>${fmtUSD(f.cost)}</span></div>
        <div class="tt-row"><span>tokens</span><span>${fmtTok(f.tokens)}</span></div>
        <div class="tt-row"><span>duracao</span><span>${fmtDuration(f.duration_seconds)}</span></div>
        <div class="tt-row"><span>share do custo</span><span>${totalAll ? (((f.cost||0)/totalAll)*100).toFixed(1) : '0.0'}%</span></div>`));
      hit.addEventListener('mouseleave', hideTip);
    });
  })();

  // ================= chart: cost per step over time (line) =================
  (function(){
    const el = $('#chart-timeline');
    if (!steps.length){ el.innerHTML = '<p style="color:var(--text-muted); font-size:13px;">sem dados</p>'; return; }
    const W = el.clientWidth || 1000, H = 300;
    const M = {top:16, right:16, bottom:30, left:56};
    const plotW = W - M.left - M.right, plotH = H - M.top - M.bottom;
    const n = steps.length;
    const maxV = Math.max(...steps.map(s => s.cost), 0.0001) * 1.15;
    const x = i => n === 1 ? M.left + plotW/2 : M.left + (i/(n-1)) * plotW;
    const y = v => M.top + plotH - (v/maxV)*plotH;

    let svg = `<svg viewBox="0 0 ${W} ${H}" width="100%" height="${H}">`;
    const ticks = 4;
    for(let i=0;i<=ticks;i++){
      const yy = M.top + plotH - (plotH*i/ticks);
      svg += `<line class="grid-line" x1="${M.left}" x2="${W-M.right}" y1="${yy}" y2="${yy}"/>`;
      svg += `<text class="axis-label" x="4" y="${yy+4}">${fmtUSD2(maxV*i/ticks)}</text>`;
    }
    svg += `<line class="baseline" x1="${M.left}" x2="${W-M.right}" y1="${M.top+plotH}" y2="${M.top+plotH}"/>`;

    let path = '';
    steps.forEach((s,i) => {
      const px = x(i), py = y(s.cost);
      path += (i===0?'M':'L') + px.toFixed(1) + ' ' + py.toFixed(1) + ' ';
    });
    svg += `<path d="${path}" fill="none" stroke="var(--s1)" stroke-width="2" stroke-linejoin="round" stroke-linecap="round"/>`;

    steps.forEach((s,i) => {
      const px = x(i), py = y(s.cost);
      if (s.outcome === 'error'){
        svg += `<circle cx="${px}" cy="${py}" r="6" fill="var(--surface-1)" stroke="var(--critical)" stroke-width="2.5"/>`;
      } else {
        svg += `<circle cx="${px}" cy="${py}" r="2.5" fill="var(--s1)"/>`;
      }
      svg += `<rect class="hit" data-i="${i}" x="${px-6}" y="${M.top}" width="12" height="${plotH}"/>`;
    });
    svg += `</svg>`;
    el.innerHTML = svg;
    el.querySelectorAll('.hit').forEach(hit => {
      const i = +hit.dataset.i, s = steps[i];
      hit.addEventListener('mousemove', e => showTip(e, `<b>passo ${s.step} · ${s.command}</b>
        <div class="tt-row"><span>outcome</span><span>${s.outcome}</span></div>
        <div class="tt-row"><span>duracao</span><span>${fmtDuration(s.duration_seconds)}</span></div>
        <div class="tt-row"><span>custo</span><span>${fmtUSD(s.cost)}</span></div>
        <div class="tt-row"><span>tokens</span><span>${fmtTok(s.tokens)}</span></div>`));
      hit.addEventListener('mouseleave', hideTip);
    });
  })();
})();
</script>
</body>
</html>
"""


def escape_for_script(payload: str) -> str:
    return payload.replace("</", "<\\/")


def render_html(report: dict) -> str:
    totals = report["totals"]
    session_short = report["session_id"][:8]
    session_scope = report.get("session_scope", {})
    session_scope_type = session_scope.get("type", "session")
    session_count = session_scope.get("session_count", 1) or 1
    session_count_label = (
        "1 sessao correlacionada"
        if session_count == 1
        else f"{session_count} sessoes correlacionadas"
    )

    title = f"Relatorio de Uso e Custo — {report['driver_label']}"
    h1 = f"{report['driver_label']} — {totals['steps']} passo(s), {fmt_usd(totals['cost_total'])}"

    cost_total_sub = ""
    if totals["cost_total"] is None:
        cost_total_sub = "sem estimativa em dolar para este driver"
    elif totals["tokens_unattributed"]:
        cost_total_sub = (
            f"inclui {fmt_usd(totals['cost_unattributed'])} pos-ultimo-passo"
        )

    replacements = {
        "__TITLE__": title,
        "__H1__": h1,
        "__DRIVER__": report["driver"],
        "__DRIVER_LABEL__": report["driver_label"],
        "__SESSION_SHORT__": session_short,
        "__SESSION_SCOPE_LABEL__": "raiz" if session_scope_type == "tree" else "sessao",
        "__SESSION_COUNT__": str(session_count),
        "__SESSION_COUNT_LABEL__": session_count_label,
        "__SESSION_FLAG__": "--session-tree"
        if session_scope_type == "tree"
        else "--session",
        "__TRACE_FILE__": report["trace_file"],
        "__FIRST_TS__": totals["first_ts"] or "?",
        "__LAST_TS__": totals["last_ts"] or "?",
        "__DURATION__": fmt_mmss(totals["duration_seconds"]),
        "__GENERATED_AT__": report["generated_at"],
        "__KPI_STEPS__": str(totals["steps"]),
        "__KPI_ERRORS__": str(totals["errors"]),
        "__KPI_ERRORS_CLASS__": "crit" if totals["errors"] else "good",
        "__KPI_COST_ATTR__": fmt_usd(totals["cost_attributed"]),
        "__KPI_COST_TOTAL__": fmt_usd(totals["cost_total"]),
        "__KPI_COST_TOTAL_SUB__": cost_total_sub,
        "__KPI_TOKENS__": fmt_tokens(totals["tokens_total"]),
        "__KPI_AVG__": fmt_usd(totals["avg_cost_step"]),
        "__COMMANDS_JSON__": escape_for_script(
            json.dumps(report["commands"], ensure_ascii=False)
        ),
        "__STEPS_JSON__": escape_for_script(
            json.dumps(report["steps"], ensure_ascii=False)
        ),
        "__ERRORS_JSON__": escape_for_script(
            json.dumps(report["errors"], ensure_ascii=False)
        ),
        "__MODELS_JSON__": escape_for_script(
            json.dumps(report["models"], ensure_ascii=False)
        ),
        "__FEATURES_JSON__": escape_for_script(
            json.dumps(report["features"], ensure_ascii=False)
        ),
        "__NOTES_JSON__": escape_for_script(
            json.dumps(report["notes"], ensure_ascii=False)
        ),
    }
    html_out = HTML_TEMPLATE
    for key, value in replacements.items():
        html_out = html_out.replace(key, value)
    return html_out


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--driver",
        required=True,
        choices=sorted(USAGE_SCRIPT),
        help="IDE/driver usado na sessao",
    )
    session_scope = parser.add_mutually_exclusive_group()
    session_scope.add_argument(
        "--session",
        default=None,
        help="Session id exato a usar",
    )
    session_scope.add_argument(
        "--session-tree",
        default=None,
        help="Codex: sessao raiz e todos os subagentes descendentes",
    )
    parser.add_argument(
        "--trace-file",
        type=Path,
        default=DEFAULT_TRACE_FILE,
        help="Trace do harness a correlacionar (default: .harness/trace.jsonl)",
    )
    parser.add_argument(
        "--out-dir",
        type=Path,
        default=REPO_ROOT / "report",
        help="Pasta de saida (default: report/)",
    )
    args = parser.parse_args()

    if args.session_tree and args.driver != "codex":
        parser.error("--session-tree so e suportado com --driver codex")

    session_id = (
        args.session_tree
        or args.session
        or find_last_session(args.driver, args.trace_file)
    )
    use_session_tree = bool(args.session_tree) or (
        args.driver == "codex" and not args.session
    )
    scope_label = "Arvore de sessoes" if use_session_tree else "Sessao"
    print(f"{scope_label}: {session_id}", file=sys.stderr)

    correlate = run_correlate(
        args.driver,
        session_id,
        args.trace_file,
        session_tree=use_session_tree,
    )
    feature_correlate = run_correlate_by_feature(
        args.driver,
        session_id,
        session_tree=use_session_tree,
    )
    report = build_report(
        args.driver, session_id, args.trace_file, correlate, feature_correlate
    )
    html_out = render_html(report)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    out_path = args.out_dir / f"session-report-{args.driver}-{stamp}.html"
    out_path.write_text(html_out, encoding="utf-8")
    print(f"Relatorio gerado em {out_path}")


if __name__ == "__main__":
    main()
