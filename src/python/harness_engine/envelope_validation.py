"""Predicados determinísticos e baratos para validar se o valor devolvido pelo driver
atende à expectativa da task — ANTES de persisti-lo e seguir o flow. Falhou → task_registry
devolve um erro corretivo tipado e o driver reenvia (loop corretivo, não término mudo).

Validação semântica profunda continua sendo trabalho do juiz-LLM na avaliação; aqui mora
só o que é checável em código, com zero token.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from typing import Callable

from harness_engine.envelope import Envelope

Validator = Callable[[Envelope], "ValidationResult"]


@dataclass(frozen=True)
class ValidationResult:
    ok: bool
    reason: str = ""

    @staticmethod
    def passed() -> "ValidationResult":
        return ValidationResult(True, "")

    @staticmethod
    def fail(reason: str) -> "ValidationResult":
        return ValidationResult(False, reason)


def not_empty(expectation: str) -> Validator:
    """O primeiro arg existe e não é vazio/whitespace."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if _first_arg(envelope)
            else ValidationResult.fail(f"O argumento esperado veio vazio. Esperado: {expectation}.")
        )

    return validator


def min_lines(count: int, expectation: str) -> Validator:
    """O primeiro arg tem ao menos `count` linhas não vazias (contando `\\n` literais)."""

    def validator(envelope: Envelope) -> ValidationResult:
        lines = _lines(_first_arg(envelope))
        return (
            ValidationResult.passed()
            if lines >= count
            else ValidationResult.fail(
                f"O argumento tem {lines} linha(s) úteis, mas a task espera ao menos {count}. "
                f"Esperado: {expectation}."
            )
        )

    return validator


def contains_number(expectation: str) -> Validator:
    """O primeiro arg contém ao menos um número."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if re.search(r"\d", _first_arg(envelope))
            else ValidationResult.fail(f"O argumento não contém nenhum número. Esperado: {expectation}.")
        )

    return validator


def matches(pattern: str, expectation: str) -> Validator:
    """O primeiro arg casa com o padrão (case-insensitive)."""

    def validator(envelope: Envelope) -> ValidationResult:
        return (
            ValidationResult.passed()
            if re.search(pattern, _first_arg(envelope), re.IGNORECASE)
            else ValidationResult.fail(f"O argumento não atende ao formato esperado. Esperado: {expectation}.")
        )

    return validator


def all_of(*validators: Validator) -> Validator:
    """Composição: todos os predicados precisam passar; o primeiro que falhar dá a razão."""

    def validator(envelope: Envelope) -> ValidationResult:
        for v in validators:
            result = v(envelope)
            if not result.ok:
                return result
        return ValidationResult.passed()

    return validator


def _first_arg(envelope: Envelope) -> str:
    return envelope.args[0].strip() if envelope.args else ""


# Artefatos trafegam como string JSON de uma linha com \n literais (ver o aviso "Compact"
# dos flows) — conta tanto quebras reais quanto escapadas.
def _lines(value: str) -> int:
    parts = re.split(r"\n|\\n", value)
    return sum(1 for part in parts if part.strip())
