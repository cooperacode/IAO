"""Flow de desenvolvimento long-running (padrão "Effective harnesses for long-running
agents", Anthropic). Um inicializador (session 0) expande o brief numa lista priorizada
de features; depois um loop de sessões de contexto fresco implementa UMA feature por vez:

    start → plan → [bearings → smoke → pick → implement → verify → handoff]*

O estado que atravessa os hard resets vive em artefatos persistentes: feature_store
(feature_list.json, do harness) e o progress.txt + git (do driver). Cada task só faz
efeitos e decide o PRÓXIMO comando (o envelope de saída) — a orquestração (dispatch,
guardas globais, transporte) fica em harness_engine.

Prompts em `prompts.py`.
"""

from __future__ import annotations

import sys
from dataclasses import replace

from flows_development import prompts
from harness_engine import docs_reader, feature_store, harness_config, run_config_store, state_store
from harness_engine.envelope import Envelope
from harness_engine.run_config_store import RunConfig

# Guardas locais deste flow (o teto global do harness.json, 12, é curto demais p/ um loop).
# Poucas features + teto de passos POR feature: barra o loop implement↔verify que nunca fecha.
MAX_FEATURES = 10
STEPS_PER_FEATURE = 8

# Teto de passos efetivo passado ao harness_host (override do global): folga p/ o pior
# caso de MAX_FEATURES features gastando STEPS_PER_FEATURE cada, mais start/plan e as fronteiras.
STEP_BUDGET = MAX_FEATURES * STEPS_PER_FEATURE + 8


def _state(key: str) -> str:
    return state_store.get(key) or ""


def _docs_folder() -> str:
    return harness_config.current().docs_folder


def start() -> str:
    # Uma sessão anterior (talvez de outro driver — os tokens acabaram numa IDE e outra
    # assume) pode ter morrido no meio de uma feature. Reiniciar jogaria fora trabalho em
    # andamento; retomar é seguro e determinístico: bearings é reentrante por construção
    # (só rearma a guarda por feature) e o próximo pick() reseleciona a mesma feature,
    # ainda pendente — sem precisar saber exatamente onde a sessão anterior parou.
    if feature_store.pending_count() > 0:
        print(
            "[dev] run em andamento detectado (feature pendente); retomando via bearings em vez de resetar.",
            file=sys.stderr,
        )
        return prompts.bearings_prompt()

    # Flow PRODUTOR da feature_list: novo run apaga a do run anterior.
    feature_store.reset()
    run_config_store.reset()

    # Brief (o que construir) vem de docs/ ou, sem docs, do modo interativo.
    if not docs_reader.has_docs(_docs_folder()):
        return prompts.initializer_interactive()

    content, files = docs_reader.read(_docs_folder())
    state_store.set("origem", "docs")
    return prompts.initializer_prompt(content, files)


def plan(envelope: Envelope | None) -> str:
    features = feature_store.parse(_arg(envelope))
    if not features:
        return prompts.plan_retry_prompt()  # não interpretou → re-pede (loop corretivo)

    # Teto de features: fica com as de maior prioridade (menor número).
    capped = sorted(features, key=lambda f: (f.priority, f.id))[:MAX_FEATURES]

    # Higieniza depends_on: uma feature sobrevivente pode depender de um id cortado acima,
    # o que a bloquearia para sempre (nunca "pronta") sem que o driver tenha como saber —
    # quem cortou foi o harness, não ele. Cortar nós de um grafo já acíclico (validado em
    # feature_store.parse) não pode criar ciclo, então só a limpeza de dangling é necessária.
    capped_ids = {f.id for f in capped}
    capped = [replace(f, depends_on=tuple(d for d in f.deps if d in capped_ids)) for f in capped]

    feature_store.write(capped)

    # Comando de verificação e diretório-alvo: reidratados a cada passo de smoke/verify.
    # Fora de state.json de propósito - ver run_config_store.
    run_config_store.write(RunConfig(
        _arg_at(envelope, 1, "dotnet test"),
        _arg_at(envelope, 2, "."),
    ))

    return prompts.bearings_prompt()


def bearings(envelope: Envelope | None) -> str:
    # Nova sessão (uma feature): zera o contador da guarda por feature.
    state_store.set("feature_steps", "1")
    return prompts.smoke_prompt()


def smoke(envelope: Envelope | None) -> str:
    return _stop("guarda por feature") if _over_feature_budget() else prompts.pick_prompt()


def pick(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("guarda por feature")

    # Seleção DETERMINÍSTICA: maior prioridade entre as prontas (dependências satisfeitas).
    # O harness escolhe, não o LLM.
    next_feature = feature_store.next_pending()
    if next_feature is None:
        # pending_count() == 0 é o caso normal (handoff já teria fechado antes). Pendência
        # > 0 só é alcançável por um feature_list.json editado à mão fora do grafo validado
        # no plan (write/mark_passed não revalidam) — não finge sucesso nesse caso.
        return (
            _done()
            if feature_store.pending_count() == 0
            else _stop("dependências bloqueadas — nenhuma feature pendente está pronta")
        )

    state_store.set("current_feature_id", str(next_feature.id))
    state_store.set("current_feature_title", next_feature.title)
    return prompts.implement_prompt(next_feature)


def implement(envelope: Envelope | None) -> str:
    return _stop("guarda por feature") if _over_feature_budget() else prompts.verify_prompt()


def verify(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("guarda por feature")

    # FALHOU → volta a implementar a MESMA feature (loop de correção, limitado pela guarda).
    # PASSOU → segue para o handoff (deixar estado limpo).
    result = _arg(envelope).strip()
    if result.upper().startswith("FAIL"):
        return prompts.fix_prompt()

    if result.upper().startswith("PASS"):
        return prompts.handoff_prompt()

    return prompts.verify_retry_prompt()


def handoff(envelope: Envelope | None) -> str:
    if not _arg(envelope).strip():
        return prompts.handoff_retry_prompt()

    try:
        feature_id = int(_state("current_feature_id"))
        feature_store.mark_passed(feature_id)
    except ValueError:
        pass

    # Alguma feature ainda pendente? Sim → próxima sessão (bearings). Não → fim.
    return _done() if feature_store.all_passing() else prompts.bearings_prompt()


# --- guardas e término -------------------------------------------------


def _over_feature_budget() -> bool:
    """Incrementa o contador da sessão e sinaliza estouro do teto por feature."""
    steps = _int_or(_state("feature_steps"), 0) + 1
    state_store.set("feature_steps", str(steps))

    if steps > STEPS_PER_FEATURE:
        print(
            f"[dev] feature '{_state('current_feature_title')}' excedeu {STEPS_PER_FEATURE} passos; encerrando.",
            file=sys.stderr,
        )
        return True
    return False


def _stop(motivo: str) -> str:
    print(f"[dev] encerrado por {motivo}. feature_list em .harness/feature_list.json", file=sys.stderr)
    return "stop"


def _done() -> str:
    print(
        f"[dev] todas as {len(feature_store.load())} features passam; concluído. "
        "Estado em .harness/feature_list.json",
        file=sys.stderr,
    )
    return "stop"


def _arg(envelope: Envelope | None) -> str:
    return envelope.args[0] if envelope is not None and envelope.args else ""


def _arg_at(envelope: Envelope | None, index: int, fallback: str) -> str:
    if envelope is not None and envelope.args and len(envelope.args) > index and envelope.args[index].strip():
        return envelope.args[index]
    return fallback


def _int_or(value: str, default: int) -> int:
    try:
        return int(value)
    except ValueError:
        return default
