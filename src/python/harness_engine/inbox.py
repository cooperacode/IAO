"""Canal de entrada por arquivo — alternativa ao argv para o envelope do turno.

O transporte por argumento single-quoted (`./run-development-py.sh '<JSON>'`) tem uma
falha estrutural: se o driver-LLM esquece a aspa de fechamento, o shell entra em modo de
continuação e trava ANTES do processo rodar — nenhuma validação da engine pode pegá-lo. A
inbox tira o payload da sintaxe de aspas do shell: o agente escreve o JSON aqui com sua
ferramenta de escrita de arquivo (não passa por shell) e roda o script SEM argumentos, um
comando bare que não tem como ficar não-terminado.
"""

from __future__ import annotations

import shutil
import sys
from pathlib import Path

_DIR = ".harness"
PATH = ".harness/inbox.json"

# Rastro do último envelope consumido — evita reprocessar um JSON velho se o script rodar
# duas vezes sem reescrita, e serve de diagnóstico.
CONSUMED_PATH = ".harness/inbox.consumed.json"


def read() -> str:
    """Conteúdo bruto da inbox, ou "" se ela não existir. O parse/sanitização fica no envelope."""
    try:
        p = Path(PATH)
        if p.exists():
            return p.read_text()
    except Exception as ex:
        print(f"[Inbox] falha ao ler {PATH}: {ex}", file=sys.stderr)

    return ""


def consume() -> None:
    """Move a inbox consumida para CONSUMED_PATH após um parse bem-sucedido."""
    try:
        p = Path(PATH)
        if p.exists():
            Path(_DIR).mkdir(parents=True, exist_ok=True)
            shutil.move(str(p), CONSUMED_PATH)
    except Exception as ex:
        print(f"[Inbox] falha ao consumir {PATH}: {ex}", file=sys.stderr)
