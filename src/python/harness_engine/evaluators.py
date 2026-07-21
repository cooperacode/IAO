"""Evaluators determinísticos (Exact Match, Regex, Trajectory) que NÃO precisam de LLM.
Rodam in-process sobre trace/harness_state, custam zero token e servem de portão: só
quando passam vale a pena escalar para o juiz-LLM (economia sob a restrição de tokens).
"""

from __future__ import annotations

import re
from dataclasses import dataclass

from harness_engine.harness_state import HarnessState
from harness_engine.trace import TraceEntry, TraceOutcome


@dataclass(frozen=True)
class Score:
    """Nota de uma métrica em [0,1]. `passed` exige acerto pleno."""

    metric: str
    value: float
    detail: str = ""

    @property
    def passed(self) -> bool:
        return self.value >= 1.0


def exact_match(expected: str, actual: str) -> Score:
    return Score("exact_match", 1.0 if _norm(expected) == _norm(actual) else 0.0,
                 f'esperado="{expected}" obtido="{actual}"')


def matches_regex(pattern: str, actual: str) -> Score:
    return Score("regex", 1.0 if re.search(pattern, actual) else 0.0, pattern)


def trajectory(expected: list[str], actual: list[str]) -> Score:
    """Fração do prefixo esperado que bateu, na ordem. Um passo fora de sequência corta a
    contagem ali — trajetória é sobre caminho, não sobre conjunto."""
    matched = 0
    for e, a in zip(expected, actual):
        if e != a:
            break
        matched += 1

    value = 1.0 if len(expected) == 0 else matched / len(expected)
    return Score("trajectory", value, f"{matched}/{len(expected)} passos na ordem esperada")


def completeness(state: HarnessState, required_keys: list[str]) -> Score:
    """Todas as chaves de domínio esperadas foram preenchidas no estado final?"""
    filled = sum(1 for k in required_keys if state.data.get(k, "").strip())
    value = 1.0 if len(required_keys) == 0 else filled / len(required_keys)
    return Score("completeness", value, f"{filled}/{len(required_keys)} chaves preenchidas")


def step_budget(trace_entries: list[TraceEntry]) -> Score:
    """Terminou em `TraceOutcome.STOP` sem ter batido no teto de passos."""
    hit_budget = any(e.outcome == TraceOutcome.BUDGET for e in trace_entries)
    terminated = any(e.outcome == TraceOutcome.STOP for e in trace_entries)

    if hit_budget:
        detail = "cortado pelo teto de passos"
    elif terminated:
        detail = "concluído dentro do teto"
    else:
        detail = "não terminou"

    return Score("budget", 1.0 if not hit_budget and terminated else 0.0, detail)


def commands_of(trace_entries: list[TraceEntry], include_errors: bool = False) -> list[str]:
    """Comandos do trace na ordem, ignorando por padrão as voltas de erro corretivo."""
    return [e.command for e in trace_entries if include_errors or e.outcome != TraceOutcome.ERROR]


def _norm(value: str) -> str:
    return value.strip()
