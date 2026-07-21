"""Erros tipados do harness."""

from __future__ import annotations


class HarnessTimeoutError(Exception):
    """Estourou o timeout de execução de um passo (ver harness_config.timeout_ms).
    Lançada e capturada dentro de task_registry: vira diagnóstico no stderr + "stop" no
    stdout — o mesmo contrato de encerramento gracioso das demais guardas (teto de passos
    e de custo)."""

    def __init__(self, timeout_ms: int):
        super().__init__(f"timeout de {timeout_ms}ms excedido na execução da task; encerrando.")
