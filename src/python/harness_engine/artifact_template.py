"""Template de saída de um artefato: `skills/<name>/ARTIFACT.md` com placeholders
`{{chave}}` substituídos por valores do state_store. A forma markdown do artefato mora
junto da skill que o produz — fora do código, editável sem alterar o pacote.
Substituição pura de strings: determinística e zero token.
"""

from __future__ import annotations

import sys
from pathlib import Path

from harness_engine import path_resolver


def load(skill_name: str) -> str | None:
    """Lê o template da skill; `None` se a skill não define um (o caller decide o fallback)."""
    try:
        path = Path(path_resolver.resolve(str(Path("skills") / skill_name / "ARTIFACT.md")))
        return path.read_text() if path.exists() else None
    except Exception as ex:
        print(f"[ArtifactTemplate] falha ao ler template de {skill_name}: {ex}", file=sys.stderr)
        return None


def render(template: str, values: dict[str, str]) -> str:
    """Substitui cada `{{chave}}` pelo valor correspondente. Placeholders sem valor
    permanecem no texto — sinal visível de dado faltante, não erro silencioso."""
    result = template
    for key, value in values.items():
        result = result.replace("{{" + key + "}}", value)
    return result
