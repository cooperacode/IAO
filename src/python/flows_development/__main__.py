"""Padrão "long-running agent": inicializador + loop de sessões frescas, uma feature por
vez. Nenhuma orquestração aqui — dispatch, guardas e transporte vivem em harness_engine.

    start → plan → [bearings → smoke → pick → implement → verify → handoff]*
"""

from __future__ import annotations

import sys

from flows_development import tasks
from harness_engine import envelope_validation, harness_host

TASKS = {
    "start": lambda _envelope: tasks.start(),
    "plan": tasks.plan,
    "bearings": tasks.bearings,
    "smoke": tasks.smoke,
    "pick": tasks.pick,
    "implement": tasks.implement,
    "verify": tasks.verify,
    "handoff": tasks.handoff,
}

# Expectativa contextual por comando; recusa vira erro corretivo (o driver corrige e reenvia).
# `pick` não tem validador — não carrega artefato do driver (a seleção é do harness).
VALIDATORS = {
    "plan": envelope_validation.not_empty("o array JSON de features [{id,title,priority}]"),
    "bearings": envelope_validation.not_empty("o resumo da orientação (pwd, progress, git log)"),
    "smoke": envelope_validation.not_empty("o resultado do smoke test (init.sh)"),
    "implement": envelope_validation.not_empty("o resumo do que foi implementado"),
    "verify": envelope_validation.matches(
        r"^(PASS\b|FAIL\b)",
        "o veredito do self-verify começando com PASS ou FAIL: motivo",
    ),
    "handoff": envelope_validation.matches(
        r"^([0-9a-f]{6,40}\b|NO_GIT:\s+\S.*)$",
        "o hash do commit ou NO_GIT: motivo quando nao houver repositorio Git",
    ),
}


def main(argv: list[str]) -> int:
    # Snapshots próprios: se este flow dividir o `.harness/` com outros flows (mesmo
    # workspace), ele NÃO pode sobrescrever o last-run.* que outro flow consome. Congela no
    # seu próprio caminho.
    # max_steps: override do teto global (12) — este flow é long-running e precisa de
    # folga p/ o loop.
    return harness_host.run(
        argv,
        TASKS,
        trace_snapshot_path=".harness/last-development.trace.jsonl",
        state_snapshot_path=".harness/last-development.state.json",
        validators=VALIDATORS,
        max_steps=tasks.STEP_BUDGET,
    )


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
