"""Flow de desenvolvimento long-running (padrão "Effective harnesses for long-running
agents", Anthropic). Um inicializador (session 0) expande o brief numa lista priorizada
de features; depois um loop de sessões de contexto fresco implementa UMA feature por vez:

    start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*

O estado que atravessa os hard resets vive em artefatos persistentes: feature_store
(feature_list.json, do harness) e o progress.txt + git (do diretório-alvo). Cada task só faz
efeitos e decide o PRÓXIMO comando (o envelope de saída) — a orquestração (dispatch,
guardas globais, transporte) fica em harness_engine.

Prompts em `prompts.py`.
"""

from __future__ import annotations

import subprocess
import sys
from dataclasses import replace
from datetime import datetime, timezone
from pathlib import Path

from flows_development import prompts
from harness_engine import docs_reader, feature_store, git_command, harness_config, run_config_store, state_store
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
    if _over_feature_budget():
        return _stop("guarda por feature")

    summary = _arg(envelope).strip()
    if summary:
        state_store.set("current_feature_summary", summary)

    attempted, success, result = _try_automated_verify()
    if attempted:
        state_store.set("current_feature_verify", result)
        return _complete_verified_feature(result) if success else prompts.fix_prompt(result)

    return prompts.verify_prompt()


def verify(envelope: Envelope | None) -> str:
    if _over_feature_budget():
        return _stop("guarda por feature")

    # FALHOU → volta a implementar a MESMA feature (loop de correção, limitado pela guarda).
    # PASSOU → o harness faz o handoff determinístico (progress + git) sem gastar um turno
    # do modelo; se falhar, cai no prompt legado de reparo manual.
    result = _arg(envelope).strip()
    if result.upper().startswith("FAIL"):
        return prompts.fix_prompt(result)

    if result.upper().startswith("PASS"):
        state_store.set("current_feature_verify", result)
        return _complete_verified_feature(result)

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


def _complete_verified_feature(verify_result: str) -> str:
    ok, confirmation, failure = _try_automated_handoff(verify_result)
    if not ok:
        print(f"[dev] handoff automatico falhou: {failure}", file=sys.stderr)
        return prompts.handoff_prompt(failure)

    print(f"[dev] handoff automatico concluido: {confirmation}", file=sys.stderr)
    try:
        feature_store.mark_passed(int(_state("current_feature_id")))
    except ValueError:
        pass

    return _done() if feature_store.all_passing() else prompts.bearings_prompt()


def _try_automated_handoff(verify_result: str) -> tuple[bool, str, str | None]:
    try:
        feature_id = int(_state("current_feature_id"))
    except ValueError:
        return False, "", "feature atual ausente no state.json"

    feature = next((f for f in feature_store.load() if f.id == feature_id), None)
    title = feature.title if feature is not None else _state("current_feature_title")
    title = title or f"feature #{feature_id}"
    config = run_config_store.load()
    target_dir = _resolve_target_dir(config.target_dir)

    try:
        target_dir.mkdir(parents=True, exist_ok=True)
        _append_progress(target_dir, feature_id, title, config.verify_cmd, verify_result)
    except Exception as ex:
        return False, "", f"falha ao atualizar progress.txt: {ex}"

    rev_parse = git_command.run(target_dir, "rev-parse", "--show-toplevel")
    if rev_parse.exit_code != 0:
        return True, f"NO_GIT: {_one_line(rev_parse.error, 'diretorio-alvo fora de um repositorio Git')}", None

    add = git_command.run(target_dir, "add", "-A", "--", ".", ":(exclude).harness")
    if add.exit_code != 0:
        return False, "", f"git add falhou: {_one_line(add.error, add.output)}"

    diff = git_command.run(target_dir, "diff", "--cached", "--quiet", "--", ".", ":(exclude).harness")
    if diff.exit_code == 0:
        head = git_command.run(target_dir, "rev-parse", "--short", "HEAD")
        return True, _one_line(head.output, "NO_CHANGES") if head.exit_code == 0 else "NO_CHANGES", None
    if diff.exit_code > 1:
        return False, "", f"git diff --cached falhou: {_one_line(diff.error, diff.output)}"

    commit = git_command.run(
        target_dir, "commit", "-m", _commit_message(feature_id, title), "--", ".", ":(exclude).harness")
    if commit.exit_code != 0:
        return False, "", f"git commit falhou: {_one_line(commit.error, commit.output)}"

    status = git_command.run(target_dir, "status", "--short", "--", ".", ":(exclude).harness")
    if status.exit_code != 0:
        return False, "", f"git status falhou: {_one_line(status.error, status.output)}"
    if status.output.strip():
        return False, "", f"diretorio-alvo ainda sujo apos commit: {_one_line(status.output)}"

    head = git_command.run(target_dir, "rev-parse", "--short", "HEAD")
    if head.exit_code != 0:
        return False, "", f"commit criado, mas hash nao foi lido: {_one_line(head.error, head.output)}"
    return True, _one_line(head.output, "COMMIT_CREATED"), None


def _try_automated_verify() -> tuple[bool, bool, str]:
    try:
        feature_id = int(_state("current_feature_id"))
    except ValueError:
        return False, False, ""

    target_dir = _resolve_target_dir(run_config_store.load().target_dir)
    script = target_dir / "verify-feature.sh"
    if not script.exists():
        return False, False, ""

    try:
        proc = subprocess.run(
            ["bash", str(script), str(feature_id)],
            cwd=target_dir,
            text=True,
            capture_output=True,
            check=False,
            timeout=_verify_timeout_seconds(),
        )
    except subprocess.TimeoutExpired as ex:
        output = _coerce_output(ex.stdout)
        error = _coerce_output(ex.stderr)
        log_path = _write_verify_log(target_dir, script, feature_id, -1, True, output, error)
        return (
            True,
            False,
            f"FAIL: verify-feature.sh {feature_id} excedeu timeout ({_verify_timeout_description()})"
            + _verify_output_suffix(output, error, log_path),
        )
    except Exception as ex:
        error = str(ex)
        log_path = _write_verify_log(target_dir, script, feature_id, -1, False, "", error)
        return (
            True,
            False,
            f"FAIL: verify-feature.sh {feature_id} nao iniciou: {_snippet(error)}{_log_suffix(log_path)}",
        )

    log_path = _write_verify_log(target_dir, script, feature_id, proc.returncode, False, proc.stdout, proc.stderr)
    if proc.returncode == 0:
        return True, True, _pass_result(feature_id, proc.stdout, proc.stderr, log_path)

    return (
        True,
        False,
        f"FAIL: verify-feature.sh {feature_id} falhou (exit {proc.returncode})"
        + _verify_output_suffix(proc.stdout, proc.stderr, log_path),
    )


def _resolve_target_dir(target_dir: str) -> Path:
    return Path(target_dir or ".").resolve()


def _append_progress(
    target_dir: Path,
    feature_id: int,
    title: str,
    verify_cmd: str,
    verify_result: str,
) -> None:
    summary = _one_line(_state("current_feature_summary"), "implementacao concluida")
    verify = _one_line(verify_result, "PASS")
    command = verify_cmd.strip() or "comando de verificacao do projeto"
    line = (
        f"[{datetime.now(timezone.utc):%Y-%m-%d %H:%M} UTC] Feature #{feature_id} - {_one_line(title)}: "
        f"{summary}. Verificar com: {_one_line(command)}. Resultado: {verify}"
    )
    with (target_dir / "progress.txt").open("a", encoding="utf-8") as fh:
        fh.write(line + "\n")


def _commit_message(feature_id: int, title: str) -> str:
    suffix = _one_line(title)
    if len(suffix) > 72:
        suffix = suffix[:72].rstrip()
    return f"feat(development): complete feature #{feature_id} - {suffix}"


def _write_verify_log(
    target_dir: Path,
    script: Path,
    feature_id: int,
    exit_code: int,
    timed_out: bool,
    output: str,
    error: str,
) -> str:
    relative_path = Path(".harness/logs") / f"verify-feature-{feature_id}.log"
    try:
        log_path = relative_path.resolve()
        log_path.parent.mkdir(parents=True, exist_ok=True)
        log_path.write_text(
            "\n".join([
                f"timestampUtc: {datetime.now(timezone.utc).isoformat()}",
                f"command: bash ./verify-feature.sh {feature_id}",
                f"cwd: {target_dir}",
                f"script: {script}",
                f"exitCode: {exit_code}",
                f"timedOut: {timed_out}",
                "",
                "--- stdout ---",
                output,
                "",
                "--- stderr ---",
                error,
                "",
            ]),
            encoding="utf-8",
        )
    except Exception as ex:
        return f"log indisponivel ({_one_line(str(ex))})"

    return relative_path.as_posix()


def _verify_timeout_seconds() -> float | None:
    timeout_ms = harness_config.current().timeout_ms
    if timeout_ms <= 0:
        return None

    margin = min(500, max(1, timeout_ms // 10))
    return max(0.001, (timeout_ms - margin) / 1000)


def _verify_timeout_description() -> str:
    seconds = _verify_timeout_seconds()
    return "sem limite" if seconds is None else f"{int(seconds * 1000)}ms"


def _pass_result(feature_id: int, output: str, error: str, log_path: str) -> str:
    first_line = _first_meaningful_line(output, error)
    result = (
        _snippet(first_line)
        if first_line.upper().startswith("PASS")
        else f"PASS: verify-feature.sh {feature_id} passou"
    )
    return result + _log_suffix(log_path)


def _verify_output_suffix(output: str | None, error: str | None, log_path: str) -> str:
    text = _snippet(_first_meaningful_line(output, error))
    return _log_suffix(log_path) if not text else f": {text}{_log_suffix(log_path)}"


def _first_meaningful_line(*values: str | None) -> str:
    for value in values:
        for line in (value or "").replace("\r", "\n").split("\n"):
            if line.strip():
                return line.strip()
    return ""


def _coerce_output(value: str | bytes | None) -> str:
    if value is None:
        return ""
    if isinstance(value, bytes):
        return value.decode(errors="replace")
    return value


def _log_suffix(log_path: str) -> str:
    return f". Log: {log_path}" if log_path.strip() else ""


def _snippet(value: str, max_chars: int = 240) -> str:
    text = _one_line(value)
    return text if len(text) <= max_chars else text[:max_chars].rstrip() + "..."


def _one_line(value: str | None, fallback: str = "") -> str:
    normalized = " ".join((value or "").replace("\r", " ").replace("\n", " ").split())
    return normalized.strip() or fallback


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
