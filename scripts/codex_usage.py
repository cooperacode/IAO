#!/usr/bin/env python3
"""Extrai uso de tokens e custo estimado dos rollouts locais do Codex.

Codex grava sessoes em:

    $CODEX_HOME/sessions/YYYY/MM/DD/rollout-*.jsonl
    $CODEX_HOME/archived_sessions/rollout-*.jsonl

Este script le eventos `event_msg` com `payload.type == "token_count"`.
Para evitar dupla contagem quando Codex repete um evento apenas para atualizar
rate limits, os totais sao calculados por delta positivo de
`payload.info.total_token_usage`, nao pela soma cega de `last_token_usage`.

Uso:
    scripts/codex_usage.py
    scripts/codex_usage.py --by-session
    scripts/codex_usage.py --session <uuid> --json
    scripts/codex_usage.py --all-repos --since 2026-07-01
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

UNKNOWN_MODEL = "<unknown>"
# Fonte: paginas de modelo em https://developers.openai.com/api/docs/models/*
# (gpt-5.4, gpt-5.4-pro, gpt-5.5, gpt-5.6-sol/terra/luna), conferido em
# 2026-07-22: "prompts with >272K input tokens are priced at 2x input and
# 1.5x output". A tabela de pricing nao mostra esse numero explicitamente.
LONG_CONTEXT_THRESHOLD = 272_000


@dataclass(frozen=True)
class ModelPrice:
    input: float  # $ / 1M non-cached input tokens
    cached_input: float | None  # $ / 1M cached input tokens
    output: float  # $ / 1M output tokens
    cache_write: float | None = None  # $ / 1M cache-write tokens


# ---------------------------------------------------------------------------
# TABELA DE PRECOS -- USD por 1.000.000 de tokens.
# Fonte: https://developers.openai.com/api/docs/pricing, conferido em
# 2026-07-20. Edite quando a OpenAI alterar os precos.
#
# Para sessoes autenticadas via ChatGPT, isto e uma estimativa API-like, nao
# uma recuperacao de custo faturado real. Modelos sem preco publico ficam como
# "n/a".
# ---------------------------------------------------------------------------
PRICING: dict[str, dict[str, dict[str, ModelPrice]]] = {
    "standard": {
        "short": {
            "gpt-5.6-sol": ModelPrice(5.00, 0.50, 30.00, 6.25),
            "gpt-5.6-terra": ModelPrice(2.50, 0.25, 15.00, 3.125),
            "gpt-5.6-luna": ModelPrice(1.00, 0.10, 6.00, 1.25),
            "gpt-5.5": ModelPrice(5.00, 0.50, 30.00),
            "gpt-5.5-pro": ModelPrice(30.00, None, 180.00),
            "gpt-5.4": ModelPrice(2.50, 0.25, 15.00),
            "gpt-5.4-mini": ModelPrice(0.75, 0.075, 4.50),
            "gpt-5.4-nano": ModelPrice(0.20, 0.02, 1.25),
            "gpt-5.4-pro": ModelPrice(30.00, None, 180.00),
            "gpt-5.3-codex": ModelPrice(1.75, 0.175, 14.00),
            "chat-latest": ModelPrice(5.00, 0.50, 30.00),
        },
        "long": {
            "gpt-5.6-sol": ModelPrice(10.00, 1.00, 45.00, 12.50),
            "gpt-5.6-terra": ModelPrice(5.00, 0.50, 22.50, 6.25),
            "gpt-5.6-luna": ModelPrice(2.00, 0.20, 9.00, 2.50),
            "gpt-5.5": ModelPrice(10.00, 1.00, 45.00),
            "gpt-5.5-pro": ModelPrice(60.00, None, 270.00),
            "gpt-5.4": ModelPrice(5.00, 0.50, 22.50),
            "gpt-5.4-pro": ModelPrice(60.00, None, 270.00),
        },
    },
    "batch": {
        "short": {
            "gpt-5.6-sol": ModelPrice(2.50, 0.25, 15.00, 3.125),
            "gpt-5.6-terra": ModelPrice(1.25, 0.125, 7.50, 1.5625),
            "gpt-5.6-luna": ModelPrice(0.50, 0.05, 3.00, 0.625),
            "gpt-5.5": ModelPrice(2.50, 0.25, 15.00),
            "gpt-5.5-pro": ModelPrice(15.00, None, 90.00),
            "gpt-5.4": ModelPrice(1.25, 0.13, 7.50),
            "gpt-5.4-mini": ModelPrice(0.375, 0.0375, 2.25),
            "gpt-5.4-nano": ModelPrice(0.10, 0.01, 0.625),
            "gpt-5.4-pro": ModelPrice(15.00, None, 90.00),
        },
        "long": {
            "gpt-5.6-sol": ModelPrice(5.00, 0.50, 22.50, 6.25),
            "gpt-5.6-terra": ModelPrice(2.50, 0.25, 11.25, 3.125),
            "gpt-5.6-luna": ModelPrice(1.00, 0.10, 4.50, 1.25),
            "gpt-5.5": ModelPrice(5.00, 0.50, 22.50),
            "gpt-5.4": ModelPrice(2.50, 0.25, 11.25),
            "gpt-5.4-pro": ModelPrice(30.00, None, 135.00),
        },
    },
    "flex": {
        "short": {
            "gpt-5.6-sol": ModelPrice(2.50, 0.25, 15.00, 3.125),
            "gpt-5.6-terra": ModelPrice(1.25, 0.125, 7.50, 1.5625),
            "gpt-5.6-luna": ModelPrice(0.50, 0.05, 3.00, 0.625),
            "gpt-5.5": ModelPrice(2.50, 0.25, 15.00),
            "gpt-5.5-pro": ModelPrice(15.00, None, 90.00),
            "gpt-5.4": ModelPrice(1.25, 0.13, 7.50),
            "gpt-5.4-mini": ModelPrice(0.375, 0.0375, 2.25),
            "gpt-5.4-nano": ModelPrice(0.10, 0.01, 0.625),
            "gpt-5.4-pro": ModelPrice(15.00, None, 90.00),
        },
        "long": {
            "gpt-5.6-sol": ModelPrice(5.00, 0.50, 22.50, 6.25),
            "gpt-5.6-terra": ModelPrice(2.50, 0.25, 11.25, 3.125),
            "gpt-5.6-luna": ModelPrice(1.00, 0.10, 4.50, 1.25),
            "gpt-5.5": ModelPrice(5.00, 0.50, 22.50),
            "gpt-5.4": ModelPrice(2.50, 0.25, 11.25),
        },
    },
    "priority": {
        "short": {
            "gpt-5.6-sol": ModelPrice(10.00, 1.00, 60.00, 12.50),
            "gpt-5.6-terra": ModelPrice(5.00, 0.50, 30.00, 6.25),
            "gpt-5.6-luna": ModelPrice(2.00, 0.20, 12.00, 2.50),
            "gpt-5.5": ModelPrice(12.50, 1.25, 75.00),
            "gpt-5.4": ModelPrice(5.00, 0.50, 30.00),
            "gpt-5.4-mini": ModelPrice(1.50, 0.15, 9.00),
            "gpt-5.3-codex": ModelPrice(3.50, 0.35, 28.00),
        },
    },
}


@dataclass
class UsageTotals:
    input_tokens: int = 0
    cached_input_tokens: int = 0
    cache_write_input_tokens: int = 0
    output_tokens: int = 0
    reasoning_output_tokens: int = 0
    total_tokens: int = 0
    first_ts: str | None = None
    last_ts: str | None = None
    max_context_window: int | None = None

    def add(
        self,
        usage: dict[str, int],
        timestamp: str | None,
        context_window: int | None = None,
    ) -> None:
        self.input_tokens += usage.get("input_tokens", 0) or 0
        self.cached_input_tokens += usage.get("cached_input_tokens", 0) or 0
        self.cache_write_input_tokens += usage.get("cache_write_input_tokens", 0) or 0
        self.output_tokens += usage.get("output_tokens", 0) or 0
        self.reasoning_output_tokens += usage.get("reasoning_output_tokens", 0) or 0
        reported_total = usage.get("total_tokens")
        self.total_tokens += (
            reported_total
            if reported_total is not None
            else (usage.get("input_tokens", 0) or 0) + (usage.get("output_tokens", 0) or 0)
        )
        if timestamp:
            if self.first_ts is None or timestamp < self.first_ts:
                self.first_ts = timestamp
            if self.last_ts is None or timestamp > self.last_ts:
                self.last_ts = timestamp
        if context_window:
            self.max_context_window = max(self.max_context_window or 0, context_window)

    def merge(self, other: "UsageTotals") -> None:
        self.input_tokens += other.input_tokens
        self.cached_input_tokens += other.cached_input_tokens
        self.cache_write_input_tokens += other.cache_write_input_tokens
        self.output_tokens += other.output_tokens
        self.reasoning_output_tokens += other.reasoning_output_tokens
        self.total_tokens += other.total_tokens
        for ts in (other.first_ts,):
            if ts and (self.first_ts is None or ts < self.first_ts):
                self.first_ts = ts
        for ts in (other.last_ts,):
            if ts and (self.last_ts is None or ts > self.last_ts):
                self.last_ts = ts
        if other.max_context_window:
            self.max_context_window = max(
                self.max_context_window or 0,
                other.max_context_window,
            )

    @property
    def non_cached_input_tokens(self) -> int:
        return max(
            0,
            self.input_tokens
            - self.cached_input_tokens
            - self.cache_write_input_tokens,
        )


@dataclass
class SessionUsage:
    session_id: str
    path: Path
    cwd: str | None = None
    source: str | None = None
    model_provider: str | None = None
    first_ts: str | None = None
    last_ts: str | None = None
    totals: dict[str, UsageTotals] | None = None
    warnings: list[str] | None = None

    def __post_init__(self) -> None:
        if self.totals is None:
            self.totals = defaultdict(UsageTotals)
        if self.warnings is None:
            self.warnings = []

    @property
    def total_usage(self) -> UsageTotals:
        total = UsageTotals()
        for usage in (self.totals or {}).values():
            total.merge(usage)
        return total


@dataclass(frozen=True)
class UsageEvent:
    session_id: str
    model: str
    usage: dict[str, int]
    timestamp: str
    context_window: int | None
    path: Path
    cwd: str | None = None


def codex_home() -> Path:
    return Path(os.environ.get("CODEX_HOME") or Path.home() / ".codex")


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent


def walk_rollouts(home: Path, include_archived: bool = True) -> Iterable[Path]:
    sessions = home / "sessions"
    if sessions.is_dir():
        yield from sorted(sessions.glob("**/rollout-*.jsonl"))
    archived = home / "archived_sessions"
    if include_archived and archived.is_dir():
        yield from sorted(archived.glob("rollout-*.jsonl"))


def _safe_json_loads(line: str) -> dict | None:
    try:
        obj = json.loads(line)
    except json.JSONDecodeError:
        return None
    return obj if isinstance(obj, dict) else None


def _snapshot(raw: dict | None) -> dict[str, int] | None:
    if not isinstance(raw, dict):
        return None
    return {
        "input_tokens": raw.get("input_tokens", 0) or 0,
        "cached_input_tokens": raw.get("cached_input_tokens", 0) or 0,
        "cache_write_input_tokens": raw.get("cache_write_input_tokens", 0) or 0,
        "output_tokens": raw.get("output_tokens", 0) or 0,
        "reasoning_output_tokens": raw.get("reasoning_output_tokens", 0) or 0,
        "total_tokens": raw.get("total_tokens", 0) or 0,
    }


def _delta(
    previous: dict[str, int] | None,
    current: dict[str, int],
) -> tuple[dict[str, int], bool]:
    if previous is None:
        return current, False

    reset = any(current.get(k, 0) < previous.get(k, 0) for k in current)
    if reset:
        return current, True

    return {k: max(0, current.get(k, 0) - previous.get(k, 0)) for k in current}, False


def _remember_ts(session: SessionUsage, ts: str | None) -> None:
    if not ts:
        return
    if session.first_ts is None or ts < session.first_ts:
        session.first_ts = ts
    if session.last_ts is None or ts > session.last_ts:
        session.last_ts = ts


def load_rollout_events(
    path: Path,
    since: str | None = None,
    until: str | None = None,
) -> tuple[SessionUsage, list[UsageEvent]]:
    fallback_id = path.stem.removeprefix("rollout-")
    session = SessionUsage(session_id=fallback_id, path=path)
    events: list[UsageEvent] = []
    current_model = UNKNOWN_MODEL
    current_context_window: int | None = None
    previous_total: dict[str, int] | None = None

    try:
        lines = path.read_text().splitlines()
    except OSError as exc:
        session.warnings.append(f"nao foi possivel ler {path}: {exc}")
        return session, events

    for line in lines:
        line = line.strip()
        if not line:
            continue
        obj = _safe_json_loads(line)
        if obj is None:
            continue

        ts = obj.get("timestamp")
        typ = obj.get("type")
        payload = obj.get("payload") if isinstance(obj.get("payload"), dict) else {}

        if typ == "session_meta":
            session.session_id = payload.get("id") or payload.get("session_id") or session.session_id
            session.cwd = payload.get("cwd") or session.cwd
            session.source = payload.get("source") or session.source
            session.model_provider = payload.get("model_provider") or session.model_provider
            _remember_ts(session, payload.get("timestamp") or ts)
            continue

        if typ == "turn_context":
            current_model = payload.get("model") or current_model
            session.cwd = payload.get("cwd") or session.cwd
            current_context_window = payload.get("model_context_window") or current_context_window
            _remember_ts(session, ts)
            continue

        if typ != "event_msg" or payload.get("type") != "token_count":
            continue

        info = payload.get("info")
        if not isinstance(info, dict):
            continue

        current_context_window = (
            info.get("model_context_window") or current_context_window
        )
        current_total = _snapshot(info.get("total_token_usage"))
        if current_total is None:
            continue
        if not ts:
            continue

        usage_delta, reset = _delta(previous_total, current_total)
        previous_total = current_total
        if reset:
            session.warnings.append(
                f"contador de tokens reiniciou em {path.name}; somando novo acumulado"
            )

        if since and ts and ts < since:
            continue
        if until and ts and ts > until:
            continue

        if all((usage_delta.get(k, 0) or 0) == 0 for k in usage_delta):
            continue

        model = current_model or UNKNOWN_MODEL
        events.append(
            UsageEvent(
                session_id=session.session_id,
                model=model,
                usage=usage_delta,
                timestamp=ts,
                context_window=current_context_window,
                path=path,
                cwd=session.cwd,
            )
        )
        _remember_ts(session, ts)

    return session, events


def load_rollout(
    path: Path,
    since: str | None = None,
    until: str | None = None,
) -> SessionUsage:
    session, events = load_rollout_events(path, since=since, until=until)
    for event in events:
        session.totals[event.model].add(
            event.usage,
            event.timestamp,
            event.context_window,
        )
    return session


def _resolve(p: str | Path) -> Path:
    return Path(p).expanduser().resolve(strict=False)


def _is_relative_to(path: Path, parent: Path) -> bool:
    try:
        path.relative_to(parent)
    except ValueError:
        return False
    return True


def session_matches_repo(session: SessionUsage, repo: Path | None) -> bool:
    if repo is None:
        return True
    if not session.cwd:
        return False
    cwd = _resolve(session.cwd)
    return cwd == repo or _is_relative_to(cwd, repo)


def collect_sessions(
    home: Path,
    repo: Path | None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
    include_archived: bool = True,
    dedupe: bool = True,
) -> tuple[list[SessionUsage], list[str]]:
    warnings: list[str] = []
    sessions: list[SessionUsage] = []

    for path in walk_rollouts(home, include_archived):
        session = load_rollout(path, since=since, until=until)
        warnings.extend(session.warnings or [])
        if session_filter and session.session_id != session_filter:
            continue
        if not session_matches_repo(session, repo):
            continue
        if not session.totals:
            continue
        sessions.append(session)

    if not dedupe:
        return sessions, warnings

    by_id: dict[str, SessionUsage] = {}
    for session in sessions:
        previous = by_id.get(session.session_id)
        if previous is None:
            by_id[session.session_id] = session
            continue
        previous_key = (
            previous.last_ts or "",
            previous.path.stat().st_size if previous.path.exists() else 0,
        )
        current_key = (
            session.last_ts or "",
            session.path.stat().st_size if session.path.exists() else 0,
        )
        if current_key > previous_key:
            by_id[session.session_id] = session
        warnings.append(
            "sessao duplicada ignorada: "
            f"{session.session_id} ({previous.path} / {session.path})"
        )

    return sorted(by_id.values(), key=lambda s: s.first_ts or ""), warnings


def iter_usage_events(
    home: Path,
    repo: Path | None = None,
    session_filter: str | None = None,
    since: str | None = None,
    until: str | None = None,
    include_archived: bool = True,
    dedupe: bool = True,
    warnings: list[str] | None = None,
):
    """Gera eventos crus de uso: (session_id, model, usage, timestamp, context_window).

    `usage` ja e o delta positivo entre snapshots acumulados do rollout. Isso
    permite correlacionar consumo por janelas externas de tempo sem reimplementar
    o parser de Codex em outros scripts.
    """
    candidates: list[tuple[SessionUsage, list[UsageEvent]]] = []
    for path in walk_rollouts(home, include_archived):
        session, events = load_rollout_events(path, since=since, until=until)
        if warnings is not None:
            warnings.extend(session.warnings or [])
        if session_filter and session.session_id != session_filter:
            continue
        if not session_matches_repo(session, repo):
            continue
        if not events:
            continue
        candidates.append((session, events))

    if dedupe:
        by_id: dict[str, tuple[SessionUsage, list[UsageEvent]]] = {}
        for session, events in candidates:
            previous = by_id.get(session.session_id)
            if previous is None:
                by_id[session.session_id] = (session, events)
                continue
            previous_session, _ = previous
            previous_key = (
                previous_session.last_ts or "",
                previous_session.path.stat().st_size
                if previous_session.path.exists()
                else 0,
            )
            current_key = (
                session.last_ts or "",
                session.path.stat().st_size if session.path.exists() else 0,
            )
            if current_key > previous_key:
                by_id[session.session_id] = (session, events)
            if warnings is not None:
                warnings.append(
                    "sessao duplicada ignorada: "
                    f"{session.session_id} ({previous_session.path} / {session.path})"
                )
        candidates = list(by_id.values())

    for _, events in sorted(
        candidates,
        key=lambda item: min((event.timestamp for event in item[1]), default=""),
    ):
        for event in sorted(events, key=lambda event: event.timestamp):
            yield (
                event.session_id,
                event.model,
                event.usage,
                event.timestamp,
                event.context_window,
            )


def per_model(sessions: list[SessionUsage]) -> dict[str, UsageTotals]:
    out: dict[str, UsageTotals] = defaultdict(UsageTotals)
    for session in sessions:
        for model, usage in (session.totals or {}).items():
            out[model].merge(usage)
    return out


def context_bucket(context_rate: str, usage: UsageTotals) -> str:
    if context_rate in ("short", "long"):
        return context_rate
    if (usage.max_context_window or 0) > LONG_CONTEXT_THRESHOLD:
        return "long"
    return "short"


def price_for(model: str, tier: str, context: str) -> ModelPrice | None:
    tier_prices = PRICING.get(tier, {})
    price = tier_prices.get(context, {}).get(model)
    if price is not None:
        return price
    if context == "long":
        return tier_prices.get("short", {}).get(model)
    return None


def estimate_cost(
    usage: UsageTotals,
    model: str,
    tier: str,
    context_rate: str,
) -> float | None:
    context = context_bucket(context_rate, usage)
    price = price_for(model, tier, context)
    if price is None:
        return None
    if usage.cached_input_tokens and price.cached_input is None:
        return None
    if usage.cache_write_input_tokens and price.cache_write is None:
        return None

    cache_write_cost = (
        usage.cache_write_input_tokens / 1_000_000 * (price.cache_write or 0.0)
    )
    return (
        usage.non_cached_input_tokens / 1_000_000 * price.input
        + usage.cached_input_tokens / 1_000_000 * (price.cached_input or 0.0)
        + cache_write_cost
        + usage.output_tokens / 1_000_000 * price.output
    )


def session_cost(
    session: SessionUsage,
    tier: str,
    context_rate: str,
) -> tuple[float, list[str]]:
    cost = 0.0
    unpriced: list[str] = []
    for model, usage in (session.totals or {}).items():
        value = estimate_cost(usage, model, tier, context_rate)
        if value is None:
            unpriced.append(model)
        else:
            cost += value
    return cost, sorted(unpriced)


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


def render_model_table(
    sessions: list[SessionUsage],
    tier: str,
    context_rate: str,
) -> None:
    by_model = per_model(sessions)
    rows = []
    grand = UsageTotals()
    grand_cost = 0.0
    unpriced: list[str] = []

    for model in sorted(by_model, key=lambda m: -by_model[m].total_tokens):
        usage = by_model[model]
        grand.merge(usage)
        cost = estimate_cost(usage, model, tier, context_rate)
        if cost is None:
            unpriced.append(model)
        else:
            grand_cost += cost
        rows.append(
            [
                model,
                context_bucket(context_rate, usage),
                f"{usage.input_tokens:,}",
                f"{usage.cached_input_tokens:,}",
                f"{usage.cache_write_input_tokens:,}",
                f"{usage.output_tokens:,}",
                f"{usage.reasoning_output_tokens:,}",
                f"{usage.total_tokens:,}",
                fmt_cost(cost),
            ]
        )

    print_table(
        rows,
        [
            "Model",
            "Ctx",
            "Input",
            "Cached",
            "Cache Write",
            "Output",
            "Reasoning",
            "Total",
            "Custo",
        ],
    )
    print(
        f"\nTotal geral: {grand.total_tokens:,} tokens, {fmt_cost(grand_cost)}"
        + (" (parcial -- ha modelos sem preco)" if unpriced else "")
    )
    if unpriced:
        print(
            f"Aviso: sem preco cadastrado para: {', '.join(sorted(unpriced))}",
            file=sys.stderr,
        )


def render_session_table(
    sessions: list[SessionUsage],
    tier: str,
    context_rate: str,
    show_path: bool,
) -> None:
    rows = []
    unpriced_seen: set[str] = set()
    for session in sessions:
        total = session.total_usage
        cost, unpriced = session_cost(session, tier, context_rate)
        unpriced_seen.update(unpriced)
        models = ",".join(sorted((session.totals or {}).keys()))
        row = [
            session.session_id,
            total.first_ts or session.first_ts or "?",
            total.last_ts or session.last_ts or "?",
            models,
            f"{total.total_tokens:,}",
            fmt_cost(cost) if not unpriced else f"{fmt_cost(cost)}*",
        ]
        if show_path:
            row.append(str(session.path))
        rows.append(row)

    headers = ["Session", "Primeira turn", "Ultima turn", "Modelos", "Total", "Custo"]
    if show_path:
        headers.append("Arquivo")
    print_table(rows, headers)
    if unpriced_seen:
        print(
            f"Aviso: sem preco cadastrado para: {', '.join(sorted(unpriced_seen))}",
            file=sys.stderr,
        )


def to_jsonable(usage: UsageTotals, model: str, tier: str, context_rate: str) -> dict:
    return {
        "input_tokens": usage.input_tokens,
        "cached_input_tokens": usage.cached_input_tokens,
        "cache_write_input_tokens": usage.cache_write_input_tokens,
        "non_cached_input_tokens": usage.non_cached_input_tokens,
        "output_tokens": usage.output_tokens,
        "reasoning_output_tokens": usage.reasoning_output_tokens,
        "total_tokens": usage.total_tokens,
        "first_ts": usage.first_ts,
        "last_ts": usage.last_ts,
        "max_context_window": usage.max_context_window,
        "context": context_bucket(context_rate, usage),
        "cost": estimate_cost(usage, model, tier, context_rate),
    }


def render_json(
    sessions: list[SessionUsage],
    home: Path,
    repo: Path | None,
    tier: str,
    context_rate: str,
    warnings: list[str],
) -> None:
    by_model = per_model(sessions)
    unpriced = sorted(
        model
        for model, usage in by_model.items()
        if estimate_cost(usage, model, tier, context_rate) is None
    )
    output = {
        "codex_home": str(home),
        "repo_filter": str(repo) if repo else None,
        "pricing": {
            "tier": tier,
            "context_rate": context_rate,
            "currency": "USD",
            "unit": "per 1M tokens",
        },
        "per_model": {
            model: to_jsonable(usage, model, tier, context_rate)
            for model, usage in by_model.items()
        },
        "per_session": {
            session.session_id: {
                "path": str(session.path),
                "cwd": session.cwd,
                "source": session.source,
                "model_provider": session.model_provider,
                "first_ts": session.first_ts,
                "last_ts": session.last_ts,
                "totals": {
                    model: to_jsonable(usage, model, tier, context_rate)
                    for model, usage in (session.totals or {}).items()
                },
                "cost": session_cost(session, tier, context_rate)[0],
                "unpriced_models": session_cost(session, tier, context_rate)[1],
            }
            for session in sessions
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
        "--codex-home",
        type=Path,
        default=None,
        help="Override do CODEX_HOME (default: $CODEX_HOME ou ~/.codex)",
    )
    parser.add_argument(
        "--repo",
        type=Path,
        default=None,
        help="Filtra sessoes cujo cwd esta dentro deste repo (default: repo atual)",
    )
    parser.add_argument(
        "--all-repos",
        action="store_true",
        help="Nao filtra por repo/cwd",
    )
    parser.add_argument("--session", default=None, help="Filtra por session id")
    parser.add_argument("--since", default=None, help="Data minima ISO ou YYYY-MM-DD")
    parser.add_argument("--until", default=None, help="Data maxima ISO ou YYYY-MM-DD")
    parser.add_argument(
        "--no-archived",
        action="store_true",
        help="Ignora $CODEX_HOME/archived_sessions",
    )
    parser.add_argument(
        "--no-dedupe",
        action="store_true",
        help="Nao remove rollouts duplicados com o mesmo session id",
    )
    parser.add_argument(
        "--pricing-tier",
        choices=sorted(PRICING),
        default="standard",
        help="Tabela de preco API usada na estimativa",
    )
    parser.add_argument(
        "--context-rate",
        choices=["auto", "short", "long"],
        default="auto",
        help="Escolhe preco short/long context; auto usa model_context_window > 272k",
    )
    parser.add_argument(
        "--by-session",
        action="store_true",
        help="Mostra tabela por sessao",
    )
    parser.add_argument(
        "--show-path",
        action="store_true",
        help="Inclui arquivo rollout na tabela por sessao",
    )
    parser.add_argument("--json", action="store_true", help="Saida em JSON")
    args = parser.parse_args()

    home = args.codex_home or codex_home()
    if not home.is_dir():
        print(f"CODEX_HOME nao encontrado: {home}", file=sys.stderr)
        sys.exit(1)

    repo = None if args.all_repos else _resolve(args.repo or repo_root())
    sessions, warnings = collect_sessions(
        home=home,
        repo=repo,
        session_filter=args.session,
        since=args.since,
        until=args.until,
        include_archived=not args.no_archived,
        dedupe=not args.no_dedupe,
    )

    for warning in warnings:
        print(f"Aviso: {warning}", file=sys.stderr)

    if args.json:
        render_json(
            sessions,
            home,
            repo,
            args.pricing_tier,
            args.context_rate,
            warnings,
        )
        return

    if args.by_session:
        render_session_table(
            sessions,
            args.pricing_tier,
            args.context_rate,
            args.show_path,
        )
    else:
        render_model_table(sessions, args.pricing_tier, args.context_rate)


if __name__ == "__main__":
    main()
