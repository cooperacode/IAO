"""Construção dos prompts do flow de desenvolvimento — a "estratégia" separada da máquina
de estados em `tasks.py`. Cada passo referencia o token de saída por constante (`$XXX`):
o mesmo nome que o driver preenche e devolve como arg do próximo envelope.
"""

from __future__ import annotations

from harness_engine import prompt_formatter, run_config_store, state_store
from harness_engine.envelope import Envelope, EnvelopeType
from harness_engine.feature_store import Feature

# Tokens de saída (o driver guarda o artefato do passo nestes e os devolve como args).
FEATURES = "$FEATURES"
VERIFY_CMD = "$VERIFY_CMD"
TARGET_DIR = "$TARGET_DIR"
NOTE = "$NOTE"
SMOKE = "$SMOKE"
SUMMARY = "$SUMMARY"
RESULT = "$RESULT"
COMMIT = "$COMMIT"

# Forma da feature_list embutida nos prompts.
FEATURES_SHAPE = '[{"id":1,"title":"...","priority":1,"dependsOn":[]}, ...]'


def _state(key: str) -> str:
    return state_store.get(key) or ""


# --- session 0: inicializador -------------------------------------------------


def initializer_prompt(content: str, files: list[str]) -> str:
    input_text = f"""Você é o INICIALIZADOR (session 0). A partir do brief abaixo:
1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
2. Escafolde o ambiente do projeto-alvo: crie um `init.sh` idempotente que instala dependências e sobe/builda o app, e a estrutura mínima de pastas.
3. Expanda o brief numa lista PRIORIZADA de features pequenas e verificáveis, cada uma implementável e testável isoladamente. Numere a prioridade (1 = mais alta). Se uma feature só faz sentido depois de outra(s) (ex.: precisa de um schema que outra feature cria), registre os ids delas em `dependsOn` — array vazio quando não houver dependência. O harness respeita essa ordem além da prioridade.

<brief fontes="{', '.join(files)}">
{content}
</brief>

Guarde em '{FEATURES}' um ARRAY JSON: {FEATURES_SHAPE}
(só o array, sem passes — toda feature nasce pendente). Guarde o comando de
verificação em '{VERIFY_CMD}' (ex.: `dotnet test`, `npm test`) e o diretório-alvo
em '{TARGET_DIR}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
        prompt_formatter.skills("dev-initializer"),
    )


def initializer_interactive() -> str:
    input_text = f"""Você é o INICIALIZADOR (session 0). Use a #tool:askQuestions e pergunte ao usuário:
(a) o que construir (objetivo do app), (b) o diretório-alvo e (c) o comando de
verificação (ex.: `dotnet test`, `npm test`). Depois:
1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).
2. Escafolde o ambiente: crie um `init.sh` idempotente no diretório-alvo.
3. Expanda o objetivo numa lista PRIORIZADA de features pequenas e verificáveis. Se uma depender de outra, registre os ids em `dependsOn` (array vazio quando não houver).

Guarde em '{FEATURES}' um ARRAY JSON {FEATURES_SHAPE},
o comando em '{VERIFY_CMD}' e o diretório em '{TARGET_DIR}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
        prompt_formatter.skills("dev-initializer"),
    )


def plan_retry_prompt() -> str:
    input_text = f"""Não consegui interpretar a lista de features. Reenvie em '{FEATURES}' um ARRAY JSON
válido, exatamente no formato {FEATURES_SHAPE} — só o array, sem texto ao redor.
Repita o comando `{VERIFY_CMD}` e `{TARGET_DIR}`."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "plan", (FEATURES, VERIFY_CMD, TARGET_DIR)),
    )


# --- loop por feature (uma sessão de contexto fresco) --------------------------


def bearings_prompt() -> str:
    input_text = """=== NOVA SESSÃO (contexto limpo) ===
Você é um agente de codificação começando uma sessão FRESCA. Não assuma nada da
sessão anterior — todo o estado está nos artefatos persistentes.

Oriente-se: rode `pwd`, leia o `progress.txt` e o `git log` recente para
entender o que já foi feito. Resuma o que encontrou em '""" + NOTE + "'."
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "bearings", (NOTE,)),
        prompt_formatter.skills("dev-bearings"),
    )


def smoke_prompt() -> str:
    input_text = f"""Smoke test: rode `./init.sh` no diretório-alvo ({run_config_store.load().target_dir}) e confirme
que o baseline sobe/builda sem erro antes de mexer em qualquer feature. Relate o
resultado (ok ou o erro encontrado) em '{SMOKE}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "smoke", (SMOKE,)),
        prompt_formatter.skills("dev-smoke"),
    )


def pick_prompt() -> str:
    input_text = """Baseline confirmado. Envie o comando `pick` para receber a próxima feature a
implementar (a de maior prioridade ainda pendente — o harness escolhe)."""
    return prompt_formatter.format(input_text, Envelope(EnvelopeType.COMMAND, "pick", ()))


def implement_prompt(feature: Feature) -> str:
    input_text = f"""Implemente EXCLUSIVAMENTE esta feature, de forma incremental e mínima — nada além
dela:

Feature #{feature.id} (prioridade {feature.priority}): {feature.title}

Trabalhe no diretório-alvo ({run_config_store.load().target_dir}). Ao terminar, resuma o que
implementou em '{SUMMARY}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", (SUMMARY,)),
        prompt_formatter.skills("dev-implement"),
    )


def verify_prompt() -> str:
    input_text = f"""Self-verify a feature #{_state('current_feature_id')} ({_state('current_feature_title')})
como um usuário faria: rode `{run_config_store.load().verify_cmd}` no diretório-alvo
({run_config_store.load().target_dir}) e confirme o comportamento ponta a ponta.

Responda em '{RESULT}' começando com `PASS` (tudo verde) ou `FAIL: <motivo>`."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", (RESULT,)),
        prompt_formatter.skills("dev-verify"),
    )


def verify_retry_prompt() -> str:
    input_text = f"""O veredito do self-verify não começou com `PASS` nem `FAIL`. Reexecute, se
necessário, `{run_config_store.load().verify_cmd}` no diretório-alvo ({run_config_store.load().target_dir}) e
responda em '{RESULT}' começando exatamente com `PASS` ou `FAIL: <motivo>`."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "verify", (RESULT,)),
        prompt_formatter.skills("dev-verify"),
    )


def fix_prompt() -> str:
    input_text = f"""A verificação FALHOU na feature #{_state('current_feature_id')}
({_state('current_feature_title')}). Corrija a implementação (ainda SÓ esta feature)
e resuma o ajuste em '{SUMMARY}' — em seguida verificamos de novo."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "implement", (SUMMARY,)),
        prompt_formatter.skills("dev-implement"),
    )


def handoff_prompt() -> str:
    input_text = f"""Deixe o estado LIMPO para a próxima sessão:
1. `git commit` com mensagem descritiva referenciando a feature #{_state('current_feature_id')}. Se o diretório-alvo não estiver em um repositório Git, registre isso explicitamente como `NO_GIT: <motivo>`.
2. Anexe uma linha ao `progress.txt`: feature concluída, o que foi feito e como verificar.

Confirme com o hash do commit ou `NO_GIT: <motivo>` em '{COMMIT}'."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "handoff", (COMMIT,)),
        prompt_formatter.skills("dev-handoff"),
    )


def handoff_retry_prompt() -> str:
    input_text = f"""A confirmação do handoff veio vazia. Atualize `progress.txt` no diretório-alvo
({run_config_store.load().target_dir}) e responda em '{COMMIT}' com o hash do commit ou
`NO_GIT: <motivo>` quando não houver repositório Git."""
    return prompt_formatter.format(
        input_text,
        Envelope(EnvelopeType.COMMAND, "handoff", (COMMIT,)),
        prompt_formatter.skills("dev-handoff"),
    )
