"""Contrato de dados trafegado entre o driver (agente) e a máquina de estados.

O modelo devolve este envelope como JSON; a engine faz o dispatch por `value`.

Não há campo de tokens: o driver típico é um LLM sem acesso ao `usage` da própria
requisição, então qualquer contagem auto-reportada seria confabulada. O teto de custo
usa apenas medidas que a engine atesta sozinha (passos e chars de instrução — ver
`task_registry`); tokens reais vivem nos metadados de billing do caller.
"""

from __future__ import annotations

import json
import sys
from dataclasses import dataclass


class EnvelopeType:
    """Sinais de protocolo carregados em `Envelope.type`."""

    TEXT = "text"
    TOOL = "tool"
    COMMAND = "command"
    ERROR = "error"


@dataclass(frozen=True)
class Envelope:
    type: str
    value: str
    # Tupla (não lista): imutável e comparável por valor de graça — o C# precisa de
    # Equals/GetHashCode manuais porque array é comparado por referência; aqui não.
    args: tuple[str, ...] = ()
    context: dict[str, str] | None = None

    def to_json(self) -> str:
        data: dict[str, object] = {
            "type": self.type,
            "value": self.value,
            "args": list(self.args),
        }
        if self.context is not None:
            data["context"] = self.context
        return json.dumps(data, separators=(",", ":"))

    @staticmethod
    def parse(value: str) -> "Envelope | None":
        """Parse tolerante: aceita cercas markdown e texto ao redor do objeto JSON."""
        return Envelope._try_parse(value)

    @staticmethod
    def _try_parse(value: str) -> "Envelope | None":
        try:
            if value is None or not value.strip():
                raise ValueError("O envelope JSON não pode ser nulo ou vazio.")

            root = json.loads(Envelope._sanitize(value))
            if not isinstance(root, dict):
                raise ValueError("O payload do envelope deve ser um objeto JSON.")

            type_ = root.get("type") or ""
            envelope_value = root.get("value") or ""
            if not isinstance(type_, str) or not isinstance(envelope_value, str):
                raise TypeError("'type' e 'value' devem ser strings.")

            args_raw = root.get("args")
            args: tuple[str, ...] = ()
            if isinstance(args_raw, list):
                collected: list[str] = []
                for item in args_raw:
                    if not isinstance(item, str):
                        raise TypeError("cada item de 'args' deve ser uma string.")
                    if item.strip():
                        collected.append(item)
                args = tuple(collected)

            context_raw = root.get("context")
            context: dict[str, str] | None = None
            if isinstance(context_raw, dict):
                context = {}
                for key, val in context_raw.items():
                    if not isinstance(val, str):
                        raise TypeError("cada valor de 'context' deve ser uma string.")
                    context[str(key)] = val

            return Envelope(type_, envelope_value, args, context)
        except Exception as ex:
            # Diagnóstico vai para stderr — stdout é o canal de transporte do harness
            # (o driver lê stdout como a próxima instrução) e não pode ser poluído.
            print(ex, file=sys.stderr)
            return None

    # Modelos frequentemente embrulham o JSON em cercas markdown (```json … ```)
    # ou adicionam texto ao redor. Normaliza para o objeto JSON bruto antes do parse.
    @staticmethod
    def _sanitize(value: str) -> str:
        v = value.strip()

        if v.startswith("```"):
            first_newline = v.find("\n")
            if first_newline >= 0:
                v = v[first_newline + 1 :]

            closing_fence = v.rfind("```")
            if closing_fence >= 0:
                v = v[:closing_fence]

            v = v.strip()

        start = v.find("{")
        end = v.rfind("}")
        if start >= 0 and end > start:
            v = v[start : end + 1]

        return v
