#!/usr/bin/env python3
"""Correlaciona passos do Harness.Engine com uso real de tokens do driver LLM.

O harness nao mede tokens: ele grava timestamps por passo em
`.harness/trace.jsonl`. Este script carrega eventos de uso locais do driver
externo (Claude Code ou Codex) e atribui a cada passo todo consumo ocorrido
entre o timestamp do passo anterior e o timestamp do passo atual.

Fontes de uso suportadas:
- `claude`: usa scripts/claude_usage.py e transcripts em ~/.claude/projects/...
- `codex`: usa scripts/codex_usage.py e rollouts em ~/.codex/sessions + archived
- `copilot`: usa scripts/copilot_usage.py e workspaceStorage do VS Code (GitHub
  Copilot Chat). Sem estimativa de custo em dolar (Copilot fatura por "premium
  request" com multiplicador, nao por token) -- so tokens.

Limitacoes:
- O casamento e por janela de tempo, nao por uma chave compartilhada. Se o
  trace.jsonl misturar varios runs, aponte --trace-file para um snapshot de um
  unico run.
- O passo 1 inclui todo consumo da sessao ate o timestamp do passo 1.
- Consumo apos o ultimo passo fica em "nao atribuido".
- Custos do Codex sao estimativas API-like quando a sessao usou login ChatGPT.
- Copilot nunca tem custo em dolar (sempre "n/a"); tokens podem vir de uma
  camada menos confiavel (`chatSessions`) ou nem existir (`sem-tokens") --
  ver scripts/copilot_usage.py para o significado de cada fonte.

Uso:
    scripts/harness_cost_correlate.py --session "$CLAUDE_CODE_SESSION_ID"
    scripts/harness_cost_correlate.py --usage-source codex --session <uuid>
    scripts/harness_cost_correlate.py --usage-source codex --repo . --json
    scripts/harness_cost_correlate.py --usage-source copilot --session <uuid>
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
import claude_usage as claude  # noqa: E402
import codex_usage as codex  # noqa: E402
import copilot_usage as copilot  # noqa: E402

DEFAULT_TRACE_FILE = Path(".harness/trace.jsonl")


@dataclass
class TraceStep:
    step: int
    command: str
    outcome: str
    instruction_chars: int
    timestamp: datetime


@dataclass(frozen=True)
class UsageEvent:
    timestamp: datetime
    timestamp_raw: str
    model: str
    usage: dict
    context_window: int | None = None


def parse_ts(value: str) -> datetime:
    dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if dt.tzinfo is None:
        return dt
    return dt.astimezone(timezone.utc).replace(tzinfo=None)


def load_trace(path: Path) -> list[TraceStep]:
    steps = []
    for line in path.read_text().splitlines():
        line = line.strip()
        if not line:
            continue
        obj = json.loads(line)
        steps.append(
            TraceStep(
                step=obj["step"],
                command=obj["command"],
                outcome=obj["outcome"],
                instruction_chars=obj["instructionChars"],
                timestamp=parse_ts(obj["timestamp"]),
            )
        )
    return steps


def fmt_cost(value: float | None) -> str:
    return f"${value:,.4f}" if value is not None else "n/a"


def print_table(rows: list[list[str]], headers: list[str]) -> None:
    widths = [len(h) for h in headers]
    for row in rows:
        for i, cell in enumerate(row):
            widths[i] = max(widths[i], len(cell))
    fmt = "  ".join(f"{{:<{w}}}" for w in widths)
    print(fmt.format(*headers))
    print(fmt.format(*("-" * w for w in widths)))
    for row in rows:
        print(fmt.format(*row))


def _resolve(path: str | Path) -> Path:
    return Path(path).expanduser().resolve(strict=False)


class UsageBackend:
    name: str

    def load_events(self, args: argparse.Namespace) -> tuple[list[UsageEvent], list[str]]:
        raise NotImplementedError

    def new_totals(self) -> Any:
        raise NotImplementedError

    def add_event(self, totals: Any, event: UsageEvent) -> None:
        raise NotImplementedError

    def total_tokens(self, totals: Any) -> int:
        raise NotImplementedError

    def cost(self, totals: Any, model: str) -> float | None:
        raise NotImplementedError

    def to_jsonable(self, totals: Any, model: str) -> dict:
        raise NotImplementedError

    def metadata(self) -> dict:
        return {}


class ClaudeBackend(UsageBackend):
    name = "claude"

    def load_events(self, args: argparse.Namespace) -> tuple[list[UsageEvent], list[str]]:
        project_dir = args.project_dir or claude.default_project_dir()
        if not project_dir.is_dir():
            print(f"Diretorio de sessoes Claude nao encontrado: {project_dir}", file=sys.stderr)
            sys.exit(1)

        warnings: list[str] = []
        events = [
            UsageEvent(
                timestamp=parse_ts(ts),
                timestamp_raw=ts,
                model=model,
                usage=usage,
            )
            for (_, _, model, usage, ts) in claude.iter_usage_events(
                project_dir,
                args.session,
                warnings=warnings,
            )
            if ts and model != claude.SYNTHETIC_MODEL
        ]
        events.sort(key=lambda event: event.timestamp)
        return events, warnings

    def new_totals(self) -> claude.UsageTotals:
        return claude.UsageTotals()

    def add_event(self, totals: claude.UsageTotals, event: UsageEvent) -> None:
        totals.add(event.usage, event.timestamp_raw)

    def total_tokens(self, totals: claude.UsageTotals) -> int:
        return totals.total_tokens

    def cost(self, totals: claude.UsageTotals, model: str) -> float | None:
        return totals.cost(model)

    def to_jsonable(self, totals: claude.UsageTotals, model: str) -> dict:
        return claude.to_jsonable(totals, model)

    def metadata(self) -> dict:
        return {"pricing": "scripts/claude_usage.py"}


class CodexBackend(UsageBackend):
    name = "codex"

    def __init__(self, pricing_tier: str, context_rate: str) -> None:
        self.pricing_tier = pricing_tier
        self.context_rate = context_rate

    def load_events(self, args: argparse.Namespace) -> tuple[list[UsageEvent], list[str]]:
        home = args.codex_home or codex.codex_home()
        if not home.is_dir():
            print(f"CODEX_HOME nao encontrado: {home}", file=sys.stderr)
            sys.exit(1)

        repo = None if args.all_repos else _resolve(args.repo or Path(__file__).resolve().parent.parent)
        warnings: list[str] = []
        events = [
            UsageEvent(
                timestamp=parse_ts(ts),
                timestamp_raw=ts,
                model=model,
                usage=usage,
                context_window=context_window,
            )
            for (_, model, usage, ts, context_window) in codex.iter_usage_events(
                home=home,
                repo=repo,
                session_filter=args.session,
                include_archived=not args.no_archived,
                dedupe=not args.no_dedupe,
                warnings=warnings,
            )
            if ts
        ]
        events.sort(key=lambda event: event.timestamp)
        return events, warnings

    def new_totals(self) -> codex.UsageTotals:
        return codex.UsageTotals()

    def add_event(self, totals: codex.UsageTotals, event: UsageEvent) -> None:
        totals.add(event.usage, event.timestamp_raw, event.context_window)

    def total_tokens(self, totals: codex.UsageTotals) -> int:
        return totals.total_tokens

    def cost(self, totals: codex.UsageTotals, model: str) -> float | None:
        return codex.estimate_cost(
            totals,
            model,
            self.pricing_tier,
            self.context_rate,
        )

    def to_jsonable(self, totals: codex.UsageTotals, model: str) -> dict:
        return codex.to_jsonable(
            totals,
            model,
            self.pricing_tier,
            self.context_rate,
        )

    def metadata(self) -> dict:
        return {
            "pricing": {
                "tier": self.pricing_tier,
                "context_rate": self.context_rate,
                "currency": "USD",
                "unit": "per 1M tokens",
                "source": "scripts/codex_usage.py",
            }
        }


class CopilotBackend(UsageBackend):
    name = "copilot"

    def load_events(self, args: argparse.Namespace) -> tuple[list[UsageEvent], list[str]]:
        user_dir = args.vscode_user_dir or copilot.default_vscode_user_dir()
        if not user_dir.is_dir():
            print(f"Diretorio User do VS Code nao encontrado: {user_dir}", file=sys.stderr)
            sys.exit(1)

        repo = None if args.all_repos else _resolve(args.repo or Path(__file__).resolve().parent.parent)
        warnings: list[str] = []
        events = [
            UsageEvent(
                timestamp=parse_ts(ts),
                timestamp_raw=ts,
                model=model or copilot.UNKNOWN_MODEL,
                usage=usage,
            )
            for (_, _, model, usage, ts, _source, _premium) in copilot.iter_usage_events(
                user_dir,
                repo=repo,
                session_filter=args.session,
                warnings=warnings,
            )
            if ts
        ]
        events.sort(key=lambda event: event.timestamp)
        return events, warnings

    def new_totals(self) -> copilot.UsageTotals:
        return copilot.UsageTotals()

    def add_event(self, totals: copilot.UsageTotals, event: UsageEvent) -> None:
        totals.add(event.usage, event.timestamp_raw)

    def total_tokens(self, totals: copilot.UsageTotals) -> int:
        return totals.total_tokens

    def cost(self, totals: copilot.UsageTotals, model: str) -> float | None:
        return None

    def to_jsonable(self, totals: copilot.UsageTotals, model: str) -> dict:
        return copilot.to_jsonable_totals(totals)

    def metadata(self) -> dict:
        return {
            "pricing": "sem custo em dolar -- Copilot fatura por premium request "
            "com multiplicador, nao por token (ver scripts/copilot_usage.py)",
        }


def make_backend(args: argparse.Namespace) -> UsageBackend:
    if args.usage_source == "claude":
        return ClaudeBackend()
    if args.usage_source == "copilot":
        return CopilotBackend()
    return CodexBackend(args.pricing_tier, args.context_rate)


def correlate(
    steps: list[TraceStep],
    events: list[UsageEvent],
    backend: UsageBackend,
) -> tuple[list[dict[str, Any]], dict[str, Any]]:
    """Retorna (por_passo, nao_atribuido), agrupado por modelo."""
    per_step: list[dict[str, Any]] = []
    prev_ts: datetime | None = None
    for step in steps:
        by_model: dict[str, Any] = defaultdict(backend.new_totals)
        for event in events:
            if (prev_ts is None or event.timestamp > prev_ts) and event.timestamp <= step.timestamp:
                backend.add_event(by_model[event.model], event)
        per_step.append(by_model)
        prev_ts = step.timestamp

    unattributed: dict[str, Any] = defaultdict(backend.new_totals)
    if prev_ts is not None:
        for event in events:
            if event.timestamp > prev_ts:
                backend.add_event(unattributed[event.model], event)

    return per_step, unattributed


def step_totals(
    by_model: dict[str, Any],
    backend: UsageBackend,
) -> tuple[int, float, list[str]]:
    tokens = sum(backend.total_tokens(usage) for usage in by_model.values())
    cost = 0.0
    unpriced = []
    for model, usage in by_model.items():
        c = backend.cost(usage, model)
        if c is None:
            unpriced.append(model)
        else:
            cost += c
    return tokens, cost, sorted(unpriced)


def render_table(
    steps: list[TraceStep],
    per_step: list[dict[str, Any]],
    unattributed: dict[str, Any],
    backend: UsageBackend,
) -> None:
    rows = []
    unpriced_seen: set[str] = set()
    grand_tokens = 0
    grand_cost = 0.0
    for step, by_model in zip(steps, per_step):
        tokens, cost, unpriced = step_totals(by_model, backend)
        unpriced_seen.update(unpriced)
        grand_tokens += tokens
        grand_cost += cost
        rows.append(
            [
                str(step.step),
                step.command,
                step.outcome,
                f"{step.instruction_chars:,}",
                f"{tokens:,}",
                fmt_cost(cost) if not unpriced else f"{fmt_cost(cost)}*",
            ]
        )
    print_table(
        rows,
        ["Step", "Command", "Outcome", "InstructionChars", "Tokens", "Custo"],
    )
    print(
        f"\nTotal atribuido aos passos: {grand_tokens:,} tokens, {fmt_cost(grand_cost)}"
    )

    unattr_tokens, unattr_cost, unattr_unpriced = step_totals(unattributed, backend)
    unpriced_seen.update(unattr_unpriced)
    if unattr_tokens:
        print(
            f"Nao atribuido (apos o ultimo passo): {unattr_tokens:,} tokens, {fmt_cost(unattr_cost)}"
        )
    if unpriced_seen:
        print(
            f"Aviso: sem preco cadastrado para: {', '.join(sorted(unpriced_seen))}",
            file=sys.stderr,
        )


def render_json(
    steps: list[TraceStep],
    per_step: list[dict[str, Any]],
    unattributed: dict[str, Any],
    warnings: list[str],
    backend: UsageBackend,
) -> None:
    step_rows = []
    for step, by_model in zip(steps, per_step):
        tokens, cost, unpriced = step_totals(by_model, backend)
        step_rows.append(
            {
                "step": step.step,
                "command": step.command,
                "outcome": step.outcome,
                "instruction_chars": step.instruction_chars,
                "timestamp": step.timestamp.isoformat(),
                "tokens": tokens,
                "cost": cost,
                "unpriced_models": unpriced,
                "by_model": {
                    model: backend.to_jsonable(usage, model)
                    for model, usage in by_model.items()
                },
            }
        )
    unattr_tokens, unattr_cost, unattr_unpriced = step_totals(unattributed, backend)
    print(
        json.dumps(
            {
                "usage_source": backend.name,
                **backend.metadata(),
                "steps": step_rows,
                "unattributed": {
                    "tokens": unattr_tokens,
                    "cost": unattr_cost,
                    "unpriced_models": unattr_unpriced,
                    "by_model": {
                        model: backend.to_jsonable(usage, model)
                        for model, usage in unattributed.items()
                    },
                },
                "warnings": warnings,
            },
            indent=2,
            ensure_ascii=False,
        )
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--trace-file",
        type=Path,
        default=DEFAULT_TRACE_FILE,
        help="Arquivo de trace do harness (default: .harness/trace.jsonl)",
    )
    parser.add_argument(
        "--usage-source",
        choices=["claude", "codex", "copilot"],
        default="claude",
        help="Fonte local de uso de tokens (default: claude)",
    )
    parser.add_argument("--session", default=None, help="Session id do driver LLM")
    parser.add_argument(
        "--project-dir",
        type=Path,
        default=None,
        help="Claude: override do diretorio de sessoes",
    )
    parser.add_argument(
        "--codex-home",
        type=Path,
        default=None,
        help="Codex: override do CODEX_HOME",
    )
    parser.add_argument(
        "--vscode-user-dir",
        type=Path,
        default=None,
        help="Copilot: override do diretorio User do VS Code",
    )
    parser.add_argument(
        "--repo",
        type=Path,
        default=None,
        help="Codex/Copilot: filtra sessoes cujo cwd/workspace esta neste repo (default: repo atual)",
    )
    parser.add_argument(
        "--all-repos",
        action="store_true",
        help="Codex/Copilot: nao filtra por repo/cwd/workspace",
    )
    parser.add_argument(
        "--no-archived",
        action="store_true",
        help="Codex: ignora $CODEX_HOME/archived_sessions",
    )
    parser.add_argument(
        "--no-dedupe",
        action="store_true",
        help="Codex: nao remove rollouts duplicados com o mesmo session id",
    )
    parser.add_argument(
        "--pricing-tier",
        choices=sorted(codex.PRICING),
        default="standard",
        help="Codex: tabela de preco API usada na estimativa",
    )
    parser.add_argument(
        "--context-rate",
        choices=["auto", "short", "long"],
        default="auto",
        help="Codex: escolhe preco short/long context",
    )
    parser.add_argument("--json", action="store_true", help="Saida em JSON")
    args = parser.parse_args()

    if not args.trace_file.is_file():
        print(f"Arquivo de trace nao encontrado: {args.trace_file}", file=sys.stderr)
        sys.exit(1)

    if not args.session:
        print(
            "--session nao informado; eventos de qualquer sessao elegivel podem cair nas janelas",
            file=sys.stderr,
        )

    steps = load_trace(args.trace_file)
    backend = make_backend(args)
    events, warnings = backend.load_events(args)

    for warning in warnings:
        print(f"Aviso: {warning}", file=sys.stderr)

    per_step, unattributed = correlate(steps, events, backend)

    if args.json:
        render_json(steps, per_step, unattributed, warnings, backend)
    else:
        render_table(steps, per_step, unattributed, backend)


if __name__ == "__main__":
    main()
