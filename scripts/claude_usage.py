#!/usr/bin/env python3
"""Extrai consumo real de tokens e custo estimado dos transcripts locais do
Claude Code para este projeto (~/.claude/projects/<projeto-codificado>/).

Soma diretamente os arquivos .jsonl de cada sessao e dos seus sub-agentes
(subagents/agent-*.jsonl, recursivo) em vez de confiar no roll-up
`toolUseResult.totalTokens` que o Claude Code grava no arquivo pai para
chamadas de Task -- esse roll-up reflete so o snapshot da ultima turn do
sub-agente, nao a soma real, e pode subestimar o consumo em ordens de
magnitude.

Uso:
    scripts/claude_usage.py
    scripts/claude_usage.py --by-session
    scripts/claude_usage.py --session "$CLAUDE_CODE_SESSION_ID"
    scripts/claude_usage.py --session <uuid> --show-subagents --json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

SYNTHETIC_MODEL = "<synthetic>"


@dataclass(frozen=True)
class ModelPrice:
    input: float  # $ / 1M standard input tokens
    output: float  # $ / 1M output tokens
    cache_write_5m: float  # $ / 1M tokens written to the 5-min ephemeral cache
    cache_write_1h: float  # $ / 1M tokens written to the 1-hour ephemeral cache
    cache_read: float  # $ / 1M tokens read from cache


# ---------------------------------------------------------------------------
# TABELA DE PRECOS -- USD por 1.000.000 de tokens. Editar a mao quando a
# Anthropic mudar os precos; nada mais no script precisa mudar.
# Fonte: platform.claude.com/docs/en/pricing, conferido em 2026-07-19.
# claude-sonnet-5 esta em preco promocional ($2/$10) ate 2026-08-31 --
# trocar pela linha comentada (preco padrao $3/$15) depois dessa data.
# ---------------------------------------------------------------------------
PRICING: dict[str, ModelPrice] = {
    "claude-fable-5": ModelPrice(
        input=10.00,
        output=50.00,
        cache_write_5m=12.50,
        cache_write_1h=20.00,
        cache_read=1.00,
    ),
    "claude-mythos-5": ModelPrice(
        input=10.00,
        output=50.00,
        cache_write_5m=12.50,
        cache_write_1h=20.00,
        cache_read=1.00,
    ),
    "claude-opus-4-8": ModelPrice(
        input=5.00,
        output=25.00,
        cache_write_5m=6.25,
        cache_write_1h=10.00,
        cache_read=0.50,
    ),
    "claude-opus-4-7": ModelPrice(
        input=5.00,
        output=25.00,
        cache_write_5m=6.25,
        cache_write_1h=10.00,
        cache_read=0.50,
    ),
    "claude-opus-4-6": ModelPrice(
        input=5.00,
        output=25.00,
        cache_write_5m=6.25,
        cache_write_1h=10.00,
        cache_read=0.50,
    ),
    "claude-opus-4-5": ModelPrice(
        input=5.00,
        output=25.00,
        cache_write_5m=6.25,
        cache_write_1h=10.00,
        cache_read=0.50,
    ),
    "claude-opus-4-5-20251101": ModelPrice(
        input=5.00,
        output=25.00,
        cache_write_5m=6.25,
        cache_write_1h=10.00,
        cache_read=0.50,
    ),
    "claude-opus-4-1": ModelPrice(  # deprecated, retira 2026-08-05
        input=15.00,
        output=75.00,
        cache_write_5m=18.75,
        cache_write_1h=30.00,
        cache_read=1.50,
    ),
    "claude-opus-4-1-20250805": ModelPrice(  # deprecated, retira 2026-08-05
        input=15.00,
        output=75.00,
        cache_write_5m=18.75,
        cache_write_1h=30.00,
        cache_read=1.50,
    ),
    "claude-sonnet-5": ModelPrice(
        input=2.00,
        output=10.00,
        cache_write_5m=2.50,
        cache_write_1h=4.00,
        cache_read=0.20,
    ),
    # "claude-sonnet-5": ModelPrice(input=3.00, output=15.00, cache_write_5m=3.75, cache_write_1h=6.00, cache_read=0.30),  # padrao, pos 2026-08-31
    "claude-sonnet-4-6": ModelPrice(
        input=3.00,
        output=15.00,
        cache_write_5m=3.75,
        cache_write_1h=6.00,
        cache_read=0.30,
    ),
    "claude-sonnet-4-5": ModelPrice(
        input=3.00,
        output=15.00,
        cache_write_5m=3.75,
        cache_write_1h=6.00,
        cache_read=0.30,
    ),
    "claude-sonnet-4-5-20250929": ModelPrice(
        input=3.00,
        output=15.00,
        cache_write_5m=3.75,
        cache_write_1h=6.00,
        cache_read=0.30,
    ),
    "claude-haiku-4-5": ModelPrice(
        input=1.00,
        output=5.00,
        cache_write_5m=1.25,
        cache_write_1h=2.00,
        cache_read=0.10,
    ),
    "claude-haiku-4-5-20251001": ModelPrice(
        input=1.00,
        output=5.00,
        cache_write_5m=1.25,
        cache_write_1h=2.00,
        cache_read=0.10,
    ),
    "claude-3-5-haiku-20241022": ModelPrice(  # retirado 2026-02-19, so referencia p/ sessoes antigas
        input=0.80,
        output=4.00,
        cache_write_5m=1.00,
        cache_write_1h=1.60,
        cache_read=0.08,
    ),
}


@dataclass
class UsageTotals:
    input_tokens: int = 0
    output_tokens: int = 0
    cache_creation_input_tokens: int = 0
    cache_read_input_tokens: int = 0
    ephemeral_5m: int = 0
    ephemeral_1h: int = 0
    first_ts: str | None = None
    last_ts: str | None = None

    def add(self, usage: dict, timestamp: str | None) -> None:
        self.input_tokens += usage.get("input_tokens", 0) or 0
        self.output_tokens += usage.get("output_tokens", 0) or 0
        self.cache_creation_input_tokens += (
            usage.get("cache_creation_input_tokens", 0) or 0
        )
        self.cache_read_input_tokens += usage.get("cache_read_input_tokens", 0) or 0
        creation = usage.get("cache_creation") or {}
        self.ephemeral_5m += creation.get("ephemeral_5m_input_tokens", 0) or 0
        self.ephemeral_1h += creation.get("ephemeral_1h_input_tokens", 0) or 0
        if timestamp:
            if self.first_ts is None or timestamp < self.first_ts:
                self.first_ts = timestamp
            if self.last_ts is None or timestamp > self.last_ts:
                self.last_ts = timestamp

    def merge(self, other: "UsageTotals") -> None:
        self.input_tokens += other.input_tokens
        self.output_tokens += other.output_tokens
        self.cache_creation_input_tokens += other.cache_creation_input_tokens
        self.cache_read_input_tokens += other.cache_read_input_tokens
        self.ephemeral_5m += other.ephemeral_5m
        self.ephemeral_1h += other.ephemeral_1h
        for ts in (other.first_ts,):
            if ts and (self.first_ts is None or ts < self.first_ts):
                self.first_ts = ts
        for ts in (other.last_ts,):
            if ts and (self.last_ts is None or ts > self.last_ts):
                self.last_ts = ts

    @property
    def total_tokens(self) -> int:
        return (
            self.input_tokens
            + self.output_tokens
            + self.cache_creation_input_tokens
            + self.cache_read_input_tokens
        )

    def cost(self, model: str) -> float | None:
        price = PRICING.get(model)
        if price is None:
            return None
        return (
            self.input_tokens / 1_000_000 * price.input
            + self.output_tokens / 1_000_000 * price.output
            + self.ephemeral_5m / 1_000_000 * price.cache_write_5m
            + self.ephemeral_1h / 1_000_000 * price.cache_write_1h
            + self.cache_read_input_tokens / 1_000_000 * price.cache_read
        )


def encode_project_path(path: Path) -> str:
    return re.sub(r"[^A-Za-z0-9]", "-", str(path))


def default_project_dir() -> Path:
    repo_root = Path(__file__).resolve().parent.parent
    return Path.home() / ".claude" / "projects" / encode_project_path(repo_root)


def _walk_subagents(subagents_dir: Path, session_id: str):
    if not subagents_dir.is_dir():
        return
    for agent_file in sorted(subagents_dir.glob("agent-*.jsonl")):
        agent_label = agent_file.stem
        yield agent_file, session_id, "subagent", agent_label
        yield from _walk_subagents(
            subagents_dir / agent_label / "subagents", session_id
        )


def walk_transcripts(project_dir: Path):
    for session_file in sorted(project_dir.glob("*.jsonl")):
        session_id = session_file.stem
        yield session_file, session_id, "main", "main"
        yield from _walk_subagents(project_dir / session_id / "subagents", session_id)


def iter_usage_events(
    project_dir: Path,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
    warnings: list[str] | None = None,
):
    """Percorre toda turn assistant (sessao principal + sub-agentes, recursivo) e gera
    eventos crus por linha: (session_id, agent_label, model, usage, timestamp).

    Inclui eventos do modelo <synthetic> -- quem consome decide se filtra. Extraido de
    `collect` para ser reaproveitado por ferramentas que precisam do timestamp por linha
    (ex.: scripts/harness_cost_correlate.py), nao so do total agregado.
    """
    for path, session_id, role, agent_label in walk_transcripts(project_dir):
        if session_filter and session_id != session_filter:
            continue
        expect_sidechain = role == "subagent"
        try:
            lines = path.read_text().splitlines()
        except OSError as exc:
            if warnings is not None:
                warnings.append(f"nao foi possivel ler {path}: {exc}")
            continue

        for line in lines:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if obj.get("type") != "assistant":
                continue
            if bool(obj.get("isSidechain", False)) != expect_sidechain:
                continue

            ts = obj.get("timestamp")
            if since and ts and ts < since:
                continue
            if until and ts and ts > until:
                continue

            if warnings is not None and any("compact" in k.lower() for k in obj.keys()):
                msg = f"chave relacionada a compactacao vista em {path.name} -- conferir possivel dupla contagem"
                if msg not in warnings:
                    warnings.append(msg)

            message = obj.get("message") or {}
            usage = message.get("usage")
            model = message.get("model")
            if not usage or not model:
                continue

            yield session_id, agent_label, model, usage, ts


def collect(
    project_dir: Path,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
):
    totals: dict[tuple[str, str, str], UsageTotals] = defaultdict(UsageTotals)
    synthetic: dict[tuple[str, str], UsageTotals] = defaultdict(UsageTotals)
    warnings: list[str] = []

    for session_id, agent_label, model, usage, ts in iter_usage_events(
        project_dir, session_filter, since, until, warnings
    ):
        if model == SYNTHETIC_MODEL:
            synthetic[(session_id, agent_label)].add(usage, ts)
        else:
            totals[(session_id, agent_label, model)].add(usage, ts)

    return totals, synthetic, warnings


def per_model(totals: dict) -> dict[str, UsageTotals]:
    out: dict[str, UsageTotals] = defaultdict(UsageTotals)
    for (_, _, model), usage in totals.items():
        out[model].merge(usage)
    return out


def per_session(totals: dict) -> dict[str, UsageTotals]:
    out: dict[str, UsageTotals] = defaultdict(UsageTotals)
    for (session_id, _, _), usage in totals.items():
        out[session_id].merge(usage)
    return out


def per_session_and_agent(totals: dict) -> dict[str, dict[str, UsageTotals]]:
    out: dict[str, dict[str, UsageTotals]] = defaultdict(
        lambda: defaultdict(UsageTotals)
    )
    for (session_id, agent_label, _), usage in totals.items():
        out[session_id][agent_label].merge(usage)
    return out


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


def render_model_table(totals: dict) -> None:
    by_model = per_model(totals)
    rows = []
    grand = UsageTotals()
    for model in sorted(by_model, key=lambda m: -by_model[m].total_tokens):
        usage = by_model[model]
        grand.merge(usage)
        rows.append(
            [
                model,
                f"{usage.input_tokens:,}",
                f"{usage.output_tokens:,}",
                f"{usage.cache_creation_input_tokens:,}",
                f"{usage.cache_read_input_tokens:,}",
                f"{usage.total_tokens:,}",
                fmt_cost(usage.cost(model)),
            ]
        )
    headers = [
        "Model",
        "Input",
        "Output",
        "Cache Write",
        "Cache Read",
        "Total",
        "Custo",
    ]
    print_table(rows, headers)
    grand_cost = sum(
        (by_model[m].cost(m) or 0.0)
        for m in by_model
        if by_model[m].cost(m) is not None
    )
    unpriced = [m for m in by_model if by_model[m].cost(m) is None]
    print(
        f"\nTotal geral: {grand.total_tokens:,} tokens, {fmt_cost(grand_cost)}"
        + (" (parcial -- ha modelos sem preco)" if unpriced else "")
    )
    if unpriced:
        print(
            f"Aviso: sem preco cadastrado para: {', '.join(sorted(unpriced))}",
            file=sys.stderr,
        )


def render_session_table(totals: dict, show_subagents: bool) -> None:
    by_session = per_session(totals)
    nested = per_session_and_agent(totals) if show_subagents else {}

    sessions = sorted(by_session, key=lambda s: by_session[s].first_ts or "")
    rows = []
    for session_id in sessions:
        usage = by_session[session_id]
        cost = _session_cost(usage, totals, session_id)
        rows.append(
            [
                session_id,
                usage.first_ts or "?",
                usage.last_ts or "?",
                f"{usage.total_tokens:,}",
                fmt_cost(cost),
            ]
        )
    headers = ["Session", "Primeira turn", "Ultima turn", "Total", "Custo"]
    print_table(rows, headers)

    if show_subagents:
        for session_id in sessions:
            agents = nested.get(session_id, {})
            if len(agents) <= 1:
                continue
            print(f"\n  {session_id}:")
            for agent_label in sorted(agents, key=lambda a: (a != "main", a)):
                usage = agents[agent_label]
                print(f"    {agent_label:<40} {usage.total_tokens:>12,} tokens")


def _session_cost(usage: UsageTotals, totals: dict, session_id: str) -> float:
    models_in_session = {model for (sid, _, model) in totals if sid == session_id}
    per_model_totals = per_model(
        {k: v for k, v in totals.items() if k[0] == session_id}
    )
    return sum(
        (per_model_totals[m].cost(m) or 0.0)
        for m in models_in_session
        if per_model_totals[m].cost(m) is not None
    )


def to_jsonable(usage: UsageTotals, model: str | None = None) -> dict:
    d = {
        "input_tokens": usage.input_tokens,
        "output_tokens": usage.output_tokens,
        "cache_creation_input_tokens": usage.cache_creation_input_tokens,
        "cache_read_input_tokens": usage.cache_read_input_tokens,
        "total_tokens": usage.total_tokens,
        "first_ts": usage.first_ts,
        "last_ts": usage.last_ts,
    }
    if model is not None:
        d["cost"] = usage.cost(model)
    return d


def render_json(totals: dict, synthetic: dict, warnings: list[str]) -> None:
    by_model = per_model(totals)
    by_session = per_session(totals)
    nested = per_session_and_agent(totals)

    unpriced = sorted(m for m in by_model if by_model[m].cost(m) is None)

    output = {
        "per_model": {m: to_jsonable(u, m) for m, u in by_model.items()},
        "per_session": {
            sid: {
                "totals": to_jsonable(u),
                "cost": _session_cost(u, totals, sid),
                "agents": {a: to_jsonable(au) for a, au in nested.get(sid, {}).items()},
            }
            for sid, u in by_session.items()
        },
        "synthetic_excluded": {
            f"{sid}:{agent}": to_jsonable(u) for (sid, agent), u in synthetic.items()
        },
        "unpriced_models": unpriced,
        "warnings": warnings,
    }
    print(json.dumps(output, indent=2, ensure_ascii=False))


def main() -> None:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "--project-dir",
        type=Path,
        default=None,
        help="Override do diretorio de sessoes (default: calculado a partir da raiz do repo)",
    )
    parser.add_argument(
        "--session", default=None, help="Filtra por um session id especifico"
    )
    parser.add_argument("--since", default=None, help="Data minima (YYYY-MM-DD)")
    parser.add_argument("--until", default=None, help="Data maxima (YYYY-MM-DD)")
    parser.add_argument(
        "--by-session", action="store_true", help="Mostra tabela por sessao"
    )
    parser.add_argument(
        "--show-subagents",
        action="store_true",
        help="Detalha sub-agentes (implica --by-session)",
    )
    parser.add_argument("--json", action="store_true", help="Saida em JSON")
    args = parser.parse_args()

    project_dir = args.project_dir or default_project_dir()
    if not project_dir.is_dir():
        print(f"Diretorio de sessoes nao encontrado: {project_dir}", file=sys.stderr)
        sys.exit(1)

    totals, synthetic, warnings = collect(
        project_dir,
        session_filter=args.session,
        since=args.since,
        until=args.until,
    )

    for w in warnings:
        print(f"Aviso: {w}", file=sys.stderr)

    if args.json:
        render_json(totals, synthetic, warnings)
        return

    show_subagents = args.show_subagents
    by_session_flag = args.by_session or show_subagents

    if by_session_flag:
        render_session_table(totals, show_subagents)
    else:
        render_model_table(totals)


if __name__ == "__main__":
    main()
