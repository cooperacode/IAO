"""Dispatch domain-agnostic: parse do envelope, guarda de iteração e erro tipado."""

from __future__ import annotations

import sys
import threading
from typing import Callable, Mapping

from harness_engine import harness_config, inbox, state_store, trace
from harness_engine.envelope import Envelope
from harness_engine.envelope_validation import ValidationResult
from harness_engine.errors import HarnessTimeoutError

Action = Callable[["Envelope | None"], str]
Validator = Callable[[Envelope], ValidationResult]


def default_max_steps() -> int:
    """Teto de passos: impede loop infinito que queimaria tokens indefinidamente.
    Valor vem do harness.json (ou do default) — ver harness_config."""
    return harness_config.current().max_steps


def dispatch(
    args: list[str],
    actions: Mapping[str, Action],
    validators: Mapping[str, Validator] | None = None,
    max_steps: int | None = None,
    should_reset_on_start: Callable[[], bool] | None = None,
) -> str:
    # Argv presente → transporte clássico (retrocompatível). Argv vazio → lê o envelope da
    # inbox em arquivo, o transporte que elimina o hang de aspas do shell (ver inbox).
    from_inbox = len(args) == 0
    arg0 = args[0] if len(args) >= 1 else inbox.read()

    envelope = Envelope.parse(arg0) if arg0 and arg0.strip() else None

    # Só consome a inbox quando o parse deu certo — um JSON quebrado deve gerar o ERRO
    # corretivo e permanecer disponível para inspeção, não sumir silenciosamente.
    if from_inbox and envelope is not None:
        inbox.consume()

    if envelope is not None and envelope.value == "start":
        # Novo workflow começa do zero — estado e trace são truncados juntos. Mas um "start"
        # também chega quando uma sessão fresca (ex.: hard reset por feature do Development)
        # reabre um run em andamento — nesse caso é RETOMADA, não início, e truncar aqui
        # apagaria o trace/step acumulados de features anteriores. O flow decide via
        # should_reset_on_start (ele sabe se há trabalho pendente); sem predicado, o padrão é
        # sempre resetar (retrocompatível com flows single-shot).
        if should_reset_on_start is None or should_reset_on_start():
            state_store.reset()
            trace.reset()

        # Contexto do driver (ex.: {"driver":"claude code"}) nasce aqui e sobrevive no
        # state_store — prompt_formatter o reinjeta em toda saída até o próximo "start".
        # Independe do reset acima: mesmo numa retomada, o driver atual deve prevalecer.
        if envelope.context:
            state_store.set_context(envelope.context)

    # Guarda de iteração — hard stop sob a restrição de tokens do time.
    step = state_store.increment()

    cost_chars = state_store.load().cost_chars
    command = envelope.value if envelope is not None and envelope.value else "(unparsed)"

    result, outcome = _resolve(envelope, step, cost_chars, actions, validators, max_steps)

    # Uma linha por volta do loop: alimenta a telemetria e o evaluator de trajetória.
    trace.append(step, command, outcome, len(result))

    # O custo da instrução emitida agora só é conhecido aqui — entra no acumulado que o
    # guard do próximo turno vai checar.
    state_store.add_cost(len(result))
    return result


def _resolve(
    envelope: Envelope | None,
    step: int,
    cost_chars: int,
    actions: Mapping[str, Action],
    validators: Mapping[str, Validator] | None,
    max_steps: int | None,
) -> tuple[str, str]:
    # Teto de passos efetivo: o override por invocação (ex.: um flow long-running como o
    # Development, que precisa de mais folga) tem precedência sobre o global do harness.json.
    effective_max_steps = max_steps if max_steps is not None else default_max_steps()
    if step > effective_max_steps:
        print(f"[harness] limite de {effective_max_steps} passos atingido; encerrando.", file=sys.stderr)
        return "stop", trace.TraceOutcome.BUDGET

    # Teto de custo, segundo guard além do de passos. Chars de instrução emitida são a
    # única medida: é o que a engine atesta sozinha. Token real vive nos metadados de
    # billing do caller — um driver-LLM não tem como reportá-lo honestamente.
    config = harness_config.current()
    if config.max_instruction_chars > 0 and cost_chars > config.max_instruction_chars:
        print(
            f"[harness] limite de {config.max_instruction_chars} chars de instrução "
            f"atingido ({cost_chars}); encerrando.",
            file=sys.stderr,
        )
        return "stop", trace.TraceOutcome.BUDGET

    # Erro tipado em vez de "stop" silencioso: o modelo recebe a causa e pode reenviar o
    # comando correto (loop corretivo, não término mudo).
    if envelope is None:
        return _error_instruction("Não foi possível interpretar o JSON recebido.", actions), trace.TraceOutcome.ERROR

    action = actions.get(envelope.value)
    if action is None:
        return _error_instruction(f"O comando '{envelope.value}' não existe.", actions), trace.TraceOutcome.ERROR

    # Validação contextual: o comando existe, mas o VALOR atende à expectativa da task?
    # Falhou → mesmo caminho de erro corretivo dos casos acima; o driver corrige e reenvia.
    if validators is not None:
        validator = validators.get(envelope.value)
        if validator is not None:
            rejected = validator(envelope)
            if not rejected.ok:
                return (
                    _error_instruction(
                        f"O comando '{envelope.value}' foi recusado: {rejected.reason} "
                        "Corrija o conteúdo de 'args' e reenvie o mesmo comando.",
                        actions,
                    ),
                    trace.TraceOutcome.ERROR,
                )

    # Guarda de tempo: uma task travada (loop infinito na lógica de domínio) prenderia o
    # processo indefinidamente. _run_with_timeout impõe o teto por passo; o estouro vira
    # erro tipado, capturado aqui, e segue o mesmo caminho gracioso do corte por budget:
    # diagnóstico no stderr + "stop" no stdout (o canal lido pelo cliente IDE).
    try:
        result = _run_with_timeout(action, envelope, config.timeout_ms)
        return result, (trace.TraceOutcome.STOP if result == "stop" else trace.TraceOutcome.INSTRUCTION)
    except HarnessTimeoutError as ex:
        print(f"[harness] {ex}", file=sys.stderr)
        return "stop", trace.TraceOutcome.TIMEOUT


# A task é uma função síncrona e OPACA — não coopera com cancelamento. O Python (CPython)
# não aborta código síncrono travado com segurança (não existe Thread.Abort), então o
# único timeout preemptivo real é rodá-la noutra thread e ABANDONAR o que travar.
# threading.Thread(daemon=True) — e não concurrent.futures.ThreadPoolExecutor — porque
# desde o Python 3.9 os workers do executor são joinados num handler de atexit, o que
# travaria a saída do processo se uma task ficasse presa; uma thread daemon é abandonada
# de fato ao processo sair, o mesmo modelo do Task.Run + threadpool background do .NET.
def _run_with_timeout(action: Action, envelope: Envelope | None, timeout_ms: int) -> str:
    if timeout_ms <= 0:
        return action(envelope)  # guarda desligada — sem overhead de thread

    result_box: list[str] = []
    error_box: list[BaseException] = []

    def runner() -> None:
        try:
            result_box.append(action(envelope))
        except BaseException as ex:  # noqa: BLE001 — repropagada na thread principal abaixo
            error_box.append(ex)

    thread = threading.Thread(target=runner, daemon=True)
    thread.start()
    thread.join(timeout_ms / 1000)

    if thread.is_alive():
        raise HarnessTimeoutError(timeout_ms)

    if error_box:
        raise error_box[0]

    return result_box[0]


def _error_instruction(reason: str, actions: Mapping[str, Action]) -> str:
    valid = ", ".join(actions.keys())
    return (
        f"ERRO no protocolo do harness: {reason} Comandos válidos: {valid}. "
        "Revise o campo 'value' do seu JSON de resposta (responda apenas com o JSON, "
        "sem cercas de código nem comentários) e reenvie o comando."
    )
