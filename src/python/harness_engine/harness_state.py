"""Estado persistido entre invocações: contador de passos + dados acumulados do domínio."""

from __future__ import annotations

from dataclasses import dataclass, field


@dataclass
class HarnessState:
    step: int
    data: dict[str, str] = field(default_factory=dict)

    # Custo acumulado do run, insumo do teto de custo (ver task_registry).
    cost_chars: int = 0

    # Contexto do driver (ex.: {"driver": "claude code"}) capturado no envelope `start` —
    # sobrevive entre invocações para que prompt_formatter possa reinjetá-lo em toda saída
    # sem que cada task o repasse manualmente.
    context: dict[str, str] | None = None

    def to_dict(self) -> dict[str, object]:
        result: dict[str, object] = {
            "step": self.step,
            "data": self.data,
            "costChars": self.cost_chars,
        }
        if self.context is not None:
            result["context"] = self.context
        return result

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "HarnessState":
        data = payload.get("data")
        return HarnessState(
            step=int(payload.get("step", 0) or 0),
            data=dict(data) if isinstance(data, dict) else {},
            cost_chars=int(payload.get("costChars", 0) or 0),
            context=dict(payload["context"]) if isinstance(payload.get("context"), dict) else None,
        )
