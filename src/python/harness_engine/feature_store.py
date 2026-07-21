"""A lista de features do flow de desenvolvimento, persistida em
`.harness/feature_list.json` — o "persistent artifact" que atravessa os hard resets de
contexto: cada sessão (uma feature) lê e escreve aqui, sem depender do histórico da
conversa. Todas nascem com `Feature.passes = False`; o flow vira uma por vez até não
sobrar nenhuma pendente.

Mesma tolerância dos demais stores: ausente ou ilegível → lista vazia, nunca derruba o run.
"""

from __future__ import annotations

import json
import sys
from collections import deque
from dataclasses import dataclass, replace
from pathlib import Path

_DIR = ".harness"
_FILE_PATH = ".harness/feature_list.json"


@dataclass(frozen=True)
class Feature:
    """Uma feature do backlog de desenvolvimento: prioridade (menor = mais alta), se já
    passa e de quais outras (por id) depende.

    `depends_on` é ANULÁVEL de propósito: um `feature_list.json` gravado por uma versão
    anterior (sem `dependsOn`) continua carregando sem lançar — `deps` normaliza para
    quem consome.
    """

    id: int
    title: str
    priority: int
    passes: bool
    depends_on: tuple[int, ...] | None = None

    @property
    def deps(self) -> tuple[int, ...]:
        return self.depends_on if self.depends_on is not None else ()

    def to_dict(self) -> dict[str, object]:
        return {
            "id": self.id,
            "title": self.title,
            "priority": self.priority,
            "passes": self.passes,
            "dependsOn": list(self.depends_on) if self.depends_on is not None else None,
        }

    @staticmethod
    def from_dict(payload: dict[str, object]) -> "Feature":
        depends_on_raw = payload.get("dependsOn")
        depends_on = tuple(int(x) for x in depends_on_raw) if isinstance(depends_on_raw, list) else None
        return Feature(
            id=int(payload.get("id") or 0),
            title=str(payload.get("title") or ""),
            priority=int(payload.get("priority") or 0),
            passes=bool(payload.get("passes", False)),
            depends_on=depends_on,
        )


def write(features: list[Feature]) -> None:
    """Sobrescreve a lista inteira — usada pelo `plan` (session 0) e por mark_passed."""
    try:
        Path(_DIR).mkdir(parents=True, exist_ok=True)
        payload = {"items": [f.to_dict() for f in features]}
        Path(_FILE_PATH).write_text(json.dumps(payload, separators=(",", ":")))
    except Exception as ex:
        print(f"[FeatureStore] falha ao gravar: {ex}", file=sys.stderr)


def parse(features_json: str) -> list[Feature]:
    """Interpreta o array cru de features que o driver devolve no `plan`
    (`[{"id":1,"title":"...","priority":1}, ...]`). Força `passes = False` (toda feature
    nasce pendente) e reindexa ids ausentes/duplicados pela ordem. Lista vazia se o JSON
    não interpretar — o caller re-emite o pedido (loop corretivo), não derruba o run.
    """
    try:
        parsed = json.loads(features_json)
        if not isinstance(parsed, list) or len(parsed) == 0:
            return []

        # Reindex primeiro: depends_on só faz sentido referenciando ids já finais, não os
        # brutos (possivelmente ausentes/duplicados) que vieram do driver.
        reindexed: list[Feature] = []
        for i, raw in enumerate(parsed):
            if not isinstance(raw, dict):
                raise TypeError("cada feature deve ser um objeto JSON")
            candidate = Feature.from_dict(raw)
            fid = candidate.id if candidate.id > 0 else i + 1
            reindexed.append(replace(candidate, id=fid, passes=False, depends_on=candidate.deps))

        error = _dependency_graph_error(reindexed)
        if error is not None:
            print(f"[FeatureStore] grafo de dependências inválido: {error}", file=sys.stderr)
            return []

        return reindexed
    except Exception as ex:
        print(f"[FeatureStore] falha ao interpretar features: {ex}", file=sys.stderr)
        return []


def _dependency_graph_error(features: list[Feature]) -> str | None:
    """`None` se o grafo de `deps` é válido (todo id existe, sem ciclo); senão, uma
    descrição do problema. Kahn (ordenação topológica): sobra nó fora do conjunto
    resolvido ⇒ ciclo. Checa dangling ref primeiro — senão uma dependência fantasma seria
    contada como eternamente não-resolvida e reportada como "ciclo" quando na verdade é id
    inválido.
    """
    valid_ids = {f.id for f in features}

    dangling = [f"{f.id}->{dep}" for f in features for dep in f.deps if dep not in valid_ids]
    if dangling:
        return f"dependsOn referencia id(s) inexistente(s): {', '.join(dangling)}"

    # GroupBy tolerante (ids duplicados não são deduplicados pelo reindex): primeiro id
    # visto define o indegree, mesma escolha do lado .NET.
    indegree: dict[int, int] = {}
    for f in features:
        if f.id not in indegree:
            indegree[f.id] = len(f.deps)

    dependents: dict[int, list[int]] = {}
    for f in features:
        for dep in f.deps:
            dependents.setdefault(dep, []).append(f.id)

    queue: deque[int] = deque(fid for fid, deg in indegree.items() if deg == 0)
    resolved: set[int] = set()
    while queue:
        fid = queue.popleft()
        if fid in resolved:
            continue
        resolved.add(fid)
        for dependent in dependents.get(fid, []):
            if dependent in indegree:
                indegree[dependent] -= 1
                if indegree[dependent] == 0:
                    queue.append(dependent)

    if len(resolved) == len(indegree):
        return None

    cyclic = [str(fid) for fid in indegree if fid not in resolved]
    return f"dependência cíclica entre as features: {', '.join(cyclic)}"


def load() -> list[Feature]:
    try:
        p = Path(_FILE_PATH)
        if not p.exists():
            return []

        payload = json.loads(p.read_text())
        items = payload.get("items") if isinstance(payload, dict) else None
        if not isinstance(items, list):
            return []
        return [Feature.from_dict(item) for item in items]
    except Exception as ex:
        print(f"[FeatureStore] falha ao carregar: {ex}", file=sys.stderr)
        return []


def next_pending() -> Feature | None:
    """A próxima feature a implementar: a de maior prioridade (menor `priority`) entre as
    PRONTAS (todo id em `deps` já com `passes == True`); desempate por `id`. `None` quando
    não há pendência pronta — pode significar fim de fato (nenhuma pendência) ou
    dependências bloqueadas. "Ready set" de Kahn recalculado a cada chamada sobre a lista
    carregada — sem estrutura de grafo persistida.
    """
    features = load()
    passed = {f.id for f in features if f.passes}

    ready = [f for f in features if not f.passes and all(dep in passed for dep in f.deps)]
    if not ready:
        return None

    ready.sort(key=lambda f: (f.priority, f.id))
    return ready[0]


def mark_passed(id_: int) -> None:
    """Marca a feature como concluída e regrava a lista. No-op se o id não existe."""
    features = load()
    if not any(f.id == id_ for f in features):
        return

    write([replace(f, passes=True) if f.id == id_ else f for f in features])


def pending_count() -> int:
    """Quantas features ainda faltam (`passes == False`)."""
    return sum(1 for f in load() if not f.passes)


def all_passing() -> bool:
    """Há features e todas passaram — condição de término do loop."""
    features = load()
    return len(features) > 0 and all(f.passes for f in features)


def reset() -> None:
    """Apaga a lista do run anterior — o flow PRODUTOR reseta no seu `start`."""
    try:
        Path(_FILE_PATH).unlink(missing_ok=True)
    except Exception as ex:
        print(f"[FeatureStore] falha ao limpar: {ex}", file=sys.stderr)
