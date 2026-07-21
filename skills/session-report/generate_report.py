#!/usr/bin/env python3
"""Gera um relatorio HTML de uso e custo da sessao mais recente de um driver
(claude/codex/copilot), correlacionando `.harness/trace.jsonl` com o consumo
real de tokens via scripts/harness_cost_correlate.py. Usa o layout de
curso/material/relatorio-execucao-harness.html como base visual.

Fluxo:
    1. scripts/<driver>_usage.py --json  -> descobre a sessao mais recente
       para este repo (ou usa --session, se informado).
    2. scripts/harness_cost_correlate.py -> correlaciona os passos do trace
       do harness com o consumo de tokens daquela sessao.
    3. Renderiza o HTML em report/.

Uso:
    skills/session-report/generate_report.py --driver claude
    skills/session-report/generate_report.py --driver codex --session <uuid>
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


def find_last_session(driver: str) -> str:
    """Roda <driver>_usage.py --json (sem filtro) e retorna o session id com o
    last_ts mais recente para este repo."""
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

    return max(sessions, key=last_ts)


def run_correlate(driver: str, session_id: str, trace_file: Path) -> dict:
    if not trace_file.is_file():
        sys.exit(
            f"Trace do harness nao encontrado: {trace_file}\n"
            "Rode uma sessao do harness (dev-initializer/dev-implement/...) antes de gerar "
            "o relatorio, ou aponte --trace-file para um trace existente "
            "(ex.: .harness/last-development.trace.jsonl)."
        )
    args = [
        "--usage-source", driver,
        "--session", session_id,
        "--trace-file", str(trace_file),
    ]
    return run_json_script(CORRELATE_SCRIPT, args, "harness_cost_correlate")


def fmt_usd(v: float | None) -> str:
    return "n/d" if v is None else f"${v:,.2f}"


def fmt_int(v: int) -> str:
    return f"{v:,}".replace(",", ".")


def fmt_mmss(seconds: float | None) -> str:
    if seconds is None:
        return "?"
    m, s = divmod(round(seconds), 60)
    return f"{m}m {s:02d}s"


def build_report(driver: str, session_id: str, trace_file: Path, correlate: dict) -> dict:
    is_copilot = driver == "copilot"
    steps_raw = correlate.get("steps", [])
    unattributed = correlate.get("unattributed", {})
    warnings = list(correlate.get("warnings", []))

    model_totals: dict[str, dict] = defaultdict(lambda: {"tokens": 0, "cost": 0.0})
    unpriced_seen: set[str] = set()

    steps = []
    for s in steps_raw:
        for model, mv in s.get("by_model", {}).items():
            model_totals[model]["tokens"] += mv["total_tokens"]
            model_totals[model]["cost"] += mv.get("cost") or 0.0
        unpriced_seen.update(s.get("unpriced_models", []))
        steps.append(
            {
                "step": s["step"],
                "command": s["command"],
                "outcome": s["outcome"],
                "instruction_chars": s["instruction_chars"],
                "timestamp": s["timestamp"],
                "tokens": s["tokens"],
                "cost": s["cost"],
            }
        )

    for model, mv in unattributed.get("by_model", {}).items():
        model_totals[model]["tokens"] += mv["total_tokens"]
        model_totals[model]["cost"] += mv.get("cost") or 0.0
    unpriced_seen.update(unattributed.get("unpriced_models", []))

    models = [
        {"name": name, "tokens": v["tokens"], "cost": None if is_copilot else v["cost"]}
        for name, v in sorted(model_totals.items(), key=lambda kv: -kv[1]["tokens"])
    ]

    commands_acc: dict[str, dict] = defaultdict(lambda: {"cost": 0.0, "tokens": 0, "steps": 0, "errors": 0})
    for s in steps:
        c = commands_acc[s["command"]]
        c["cost"] += s["cost"]
        c["tokens"] += s["tokens"]
        c["steps"] += 1
        if s["outcome"] == "error":
            c["errors"] += 1
    commands = sorted(
        ({"cmd": k, **v} for k, v in commands_acc.items()),
        key=lambda c: -c["cost"],
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
        duration_seconds = (
            datetime.fromisoformat(last_ts) - datetime.fromisoformat(first_ts)
        ).total_seconds()

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
            f"{fmt_int(tokens_unattr)} tokens "
            f"({fmt_usd(None if is_copilot else cost_unattr)}) registrados apos o ultimo "
            "passo do trace, nao atribuidos a nenhum passo."
        )

    pricing = correlate.get("pricing")

    return {
        "driver": driver,
        "driver_label": DRIVER_LABEL[driver],
        "session_id": session_id,
        "trace_file": str(trace_file.relative_to(REPO_ROOT)) if trace_file.is_relative_to(REPO_ROOT) else str(trace_file),
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC"),
        "pricing": pricing if isinstance(pricing, dict) else None,
        "models": models,
        "commands": commands,
        "steps": steps,
        "errors": errors,
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
            "avg_cost_step": None if is_copilot or not steps else cost_attributed / len(steps),
            "duration_seconds": duration_seconds,
            "first_ts": first_ts,
            "last_ts": last_ts,
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
    <p class="sub">Passos de <code>__TRACE_FILE__</code> correlacionados com o consumo real de tokens da sessao <code>__SESSION_SHORT__</code> via
    <code>scripts/harness_cost_correlate.py</code>. Driver: __DRIVER_LABEL__.</p>
    <div class="meta-row">
      <span class="meta-chip">sessao <b>__SESSION_SHORT__</b></span>
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
        <div class="label">Custo total da sessao</div>
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
      <p class="section-note">Consumo agregado por modelo dentro da janela da sessao (passos + nao atribuido).</p>
    </div>
    <div class="table-scroll">
      <table>
        <thead><tr><th>Modelo</th><th class="num">Tokens</th><th class="num">Custo</th></tr></thead>
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
        <table>
          <thead>
            <tr><th class="num">Passo</th><th>Comando</th><th>Outcome</th><th class="num">Chars instrucao</th><th class="num">Tokens</th><th class="num">Custo</th></tr>
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
    <p><b>Fontes:</b> <code>__TRACE_FILE__</code> (__KPI_STEPS__ passos) correlacionado via <code>scripts/harness_cost_correlate.py --usage-source __DRIVER__ --session __SESSION_SHORT__</code>, contra a sessao mais recente de __DRIVER_LABEL__ para este repo (descoberta com <code>scripts/__DRIVER___usage.py --json</code>).</p>
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
  const notes = __NOTES_JSON__;

  const palette = ['--s1','--s2','--s3','--s4','--s5','--s6','--s7','--s8'];
  const cmdColor = {};
  commands.forEach((c,i) => { cmdColor[c.cmd] = palette[i % palette.length]; });
  const colorOf = cmd => `var(${cmdColor[cmd] || '--s1'})`;

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
      <td class="num">${fmtInt(c.tokens)}</td>
      <td class="num">${fmtUSD(c.cost)}</td>
    </tr>`).join('');

  // ---------- table: models ----------
  $('#tbl-models').innerHTML = models.map(m => `
    <tr>
      <td class="mono-cell">${m.name}</td>
      <td class="num">${fmtInt(m.tokens)}</td>
      <td class="num">${fmtUSD(m.cost)}</td>
    </tr>`).join('');

  // ---------- table: full log ----------
  $('#tbl-log').innerHTML = steps.map(s => {
    const pill = s.outcome === 'error' ? '<span class="pill err">error</span>' : '<span class="pill ok">' + s.outcome + '</span>';
    return `<tr>
      <td class="num mono-cell">${s.step}</td>
      <td><span class="cmd-tag" style="background:${colorOf(s.command)}">${s.command}</span></td>
      <td>${pill}</td>
      <td class="num">${fmtInt(s.instruction_chars)}</td>
      <td class="num">${fmtInt(s.tokens)}</td>
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
      <span>${fmtInt(s.tokens)} tokens</span>
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
        <div class="tt-row"><span>passos</span><span>${c.steps}</span></div>
        <div class="tt-row"><span>erros</span><span>${c.errors}</span></div>
        <div class="tt-row"><span>share do custo</span><span>${totalAll ? ((c.cost/totalAll)*100).toFixed(1) : '0.0'}%</span></div>`));
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
        <div class="tt-row"><span>custo</span><span>${fmtUSD(s.cost)}</span></div>
        <div class="tt-row"><span>tokens</span><span>${fmtInt(s.tokens)}</span></div>`));
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

    title = f"Relatorio de Uso e Custo — {report['driver_label']}"
    h1 = f"{report['driver_label']} — {totals['steps']} passo(s), {fmt_usd(totals['cost_total'])}"

    cost_total_sub = ""
    if totals["cost_total"] is None:
        cost_total_sub = "sem estimativa em dolar para este driver"
    elif totals["tokens_unattributed"]:
        cost_total_sub = f"inclui {fmt_usd(totals['cost_unattributed'])} pos-ultimo-passo"

    replacements = {
        "__TITLE__": title,
        "__H1__": h1,
        "__DRIVER__": report["driver"],
        "__DRIVER_LABEL__": report["driver_label"],
        "__SESSION_SHORT__": session_short,
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
        "__KPI_TOKENS__": fmt_int(totals["tokens_total"]),
        "__KPI_AVG__": fmt_usd(totals["avg_cost_step"]),
        "__COMMANDS_JSON__": escape_for_script(json.dumps(report["commands"], ensure_ascii=False)),
        "__STEPS_JSON__": escape_for_script(json.dumps(report["steps"], ensure_ascii=False)),
        "__ERRORS_JSON__": escape_for_script(json.dumps(report["errors"], ensure_ascii=False)),
        "__MODELS_JSON__": escape_for_script(json.dumps(report["models"], ensure_ascii=False)),
        "__NOTES_JSON__": escape_for_script(json.dumps(report["notes"], ensure_ascii=False)),
    }
    html_out = HTML_TEMPLATE
    for key, value in replacements.items():
        html_out = html_out.replace(key, value)
    return html_out


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--driver", required=True, choices=sorted(USAGE_SCRIPT), help="IDE/driver usado na sessao")
    parser.add_argument(
        "--session",
        default=None,
        help="Session id a usar (default: descoberta automaticamente via <driver>_usage.py, pegando a de last_ts mais recente)",
    )
    parser.add_argument(
        "--trace-file",
        type=Path,
        default=DEFAULT_TRACE_FILE,
        help="Trace do harness a correlacionar (default: .harness/trace.jsonl)",
    )
    parser.add_argument("--out-dir", type=Path, default=REPO_ROOT / "report", help="Pasta de saida (default: report/)")
    args = parser.parse_args()

    session_id = args.session or find_last_session(args.driver)
    print(f"Sessao: {session_id}", file=sys.stderr)

    correlate = run_correlate(args.driver, session_id, args.trace_file)
    report = build_report(args.driver, session_id, args.trace_file, correlate)
    html_out = render_html(report)

    args.out_dir.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    out_path = args.out_dir / f"session-report-{args.driver}-{stamp}.html"
    out_path.write_text(html_out, encoding="utf-8")
    print(f"Relatorio gerado em {out_path}")


if __name__ == "__main__":
    main()
