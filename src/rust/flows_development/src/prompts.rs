//! Construção dos prompts do flow de desenvolvimento — a "estratégia" separada da máquina
//! de estados em `tasks`. Cada passo referencia o token de saída por constante (`$XXX`):
//! o mesmo nome que o driver preenche e devolve como arg do próximo envelope.

use harness_engine::envelope::{Envelope, envelope_type};
use harness_engine::feature_store::{DESCRIPTION_MAX_CHARS, Feature};
use harness_engine::{artifact_store, prompt_formatter, run_config_store, state_store};

use crate::tasks::{BRIEF_ARTIFACT_NAME, CURRENT_FEATURE_ID_KEY, CURRENT_FEATURE_TITLE_KEY};

// Tokens de saída (o driver guarda o artefato do passo nestes e os devolve como args).
const FEATURES: &str = "$FEATURES";
const VERIFY_CMD: &str = "$VERIFY_CMD";
const TARGET_DIR: &str = "$TARGET_DIR";
const NOTE: &str = "$NOTE";
const SMOKE: &str = "$SMOKE";
const SUMMARY: &str = "$SUMMARY";
const RESULT: &str = "$RESULT";
const COMMIT: &str = "$COMMIT";

const FEATURES_SHAPE: &str =
    r#"[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]"#;

// Reinjeta description/references (feature_store::Feature) no prompt de implement — o único
// ponto do loop que recebe o Feature inteiro, não só título/id via state_store. "" quando a
// feature não tem nenhum dos dois (ex.: feature_list.json de uma versão anterior a esta, sem
// os campos) — o bloco some, não aparece com valores vazios.
fn feature_context_block(feature: &Feature) -> String {
    if feature.description.trim().is_empty() && feature.references.is_empty() {
        return "\n".to_string();
    }

    let references = if feature.references.is_empty() {
        "nenhuma".to_string()
    } else {
        feature.references.join(", ")
    };
    format!(
        "Descrição: {}\nReferências do brief: {references}\n\n",
        feature.description
    )
}

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

// Reinjeta o brief persistido (artifact_store, BRIEF_ARTIFACT_NAME) nos dois pontos do loop
// que realmente raciocinam sobre "o que construir" — bearings e implement — e só ali:
// smoke/pick/verify/fix/handoff rodam script ou fazem bookkeeping, sem necessidade de
// contexto de escopo. Devolve uma linha em branco (mesma quebra de parágrafo de antes desta
// feature) quando não há brief persistido — modo interativo, ou retomada de um run anterior a
// esta feature — ou o bloco "<brief>" quando há. Mesmo tratamento das skills
// (prompt_formatter::read_skills): quebras de linha viram o marcador literal "\n" e o bloco
// inteiro fica numa única linha — o conteúdo do brief não precisa preservar a formatação
// Markdown original aqui, só estar disponível. Reinjetar sempre o MESMO texto, byte a byte,
// também é a aposta de menor custo para se beneficiar de cache de prompt do provedor por trás
// do driver (não garantido: o harness só controla o texto emitido, não se o driver marca um
// breakpoint de cache ali).
fn brief_block() -> String {
    let brief = artifact_store::read(BRIEF_ARTIFACT_NAME);
    if brief.trim().is_empty() {
        return "\n".to_string();
    }

    let single_line = brief.replace("\r\n", "\\n").replace('\n', "\\n");
    format!("<brief>{single_line}</brief>\n")
}

// --- session 0: inicializador -------------------------------------------------

pub fn initializer_prompt(content: &str, files: &[String]) -> String {
    let sources = files.join(", ");
    let input = format!(
        "Você é o INICIALIZADOR (session 0). A partir do brief abaixo:\n\
1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).\n\
2. Escafolde o ambiente do projeto-alvo: crie um `init.sh` idempotente que instala dependências e sobe/builda o app, um `verify-feature.sh <id>` idempotente que verifica uma feature, e a estrutura mínima de pastas.\n\
3. Expanda o brief numa lista PRIORIZADA de features pequenas e verificáveis, cada uma implementável e testável isoladamente. Numere a prioridade (1 = mais alta). Se uma feature só faz sentido depois de outra(s) (ex.: precisa de um schema que outra feature cria), registre os ids delas em `dependsOn` — array vazio quando não houver dependência. O harness respeita essa ordem além da prioridade. Preencha também, para cada feature: `description`, uma descrição objetiva do que ela faz (até {DESCRIPTION_MAX_CHARS} caracteres); e `references`, os códigos explícitos citados no brief que se relacionam a ela (ex.: \"RF-003\", \"JIRA-142\", uma seção nomeada) — array vazio se o brief não citar nenhum código explícito para essa feature (não invente um).\n\
\n\
<brief fontes=\"{sources}\">\n\
{content}\n\
</brief>\n\
\n\
Guarde em '{FEATURES}' um ARRAY JSON: {FEATURES_SHAPE}\n\
(só o array, sem passes — toda feature nasce pendente). Guarde o comando de\n\
verificação em '{VERIFY_CMD}' (ex.: `dotnet test`, `npm test`) e o diretório-alvo\n\
em '{TARGET_DIR}'. O `verify-feature.sh` pode rodar a suite completa no começo:\n\
`./init.sh`, depois `$VERIFY_CMD`, imprimir `PASS: feature <id> ...` e sair 0."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "plan",
            vec![
                FEATURES.to_string(),
                VERIFY_CMD.to_string(),
                TARGET_DIR.to_string(),
            ],
        ),
        Some(&prompt_formatter::skills(&["dev-initializer"])),
    )
}

pub fn initializer_interactive() -> String {
    let input = format!(
        "Você é o INICIALIZADOR (session 0). Use a #tool:askQuestions e pergunte ao usuário:\n\
(a) o que construir (objetivo do app), (b) o diretório-alvo e (c) o comando de\n\
verificação (ex.: `dotnet test`, `npm test`). Depois:\n\
1. Garanta um repositório Git no diretório-alvo (rode `git init` se necessário) e crie/reaproveite uma branch de trabalho dedicada (nunca direto em main/master).\n\
2. Escafolde o ambiente: crie um `init.sh` idempotente e um `verify-feature.sh <id>` idempotente no diretório-alvo.\n\
3. Expanda o objetivo numa lista PRIORIZADA de features pequenas e verificáveis. Se uma depender de outra, registre os ids em `dependsOn` (array vazio quando não houver). Preencha também `description` (até {DESCRIPTION_MAX_CHARS} caracteres) e `references` (códigos explícitos citados pelo usuário para essa feature; array vazio se não houver nenhum).\n\
\n\
Guarde em '{FEATURES}' um ARRAY JSON {FEATURES_SHAPE},\n\
o comando em '{VERIFY_CMD}' e o diretório em '{TARGET_DIR}'. O `verify-feature.sh`\n\
pode rodar a suite completa no começo: `./init.sh`, depois `$VERIFY_CMD`, imprimir\n\
`PASS: feature <id> ...` e sair 0."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "plan",
            vec![
                FEATURES.to_string(),
                VERIFY_CMD.to_string(),
                TARGET_DIR.to_string(),
            ],
        ),
        Some(&prompt_formatter::skills(&["dev-initializer"])),
    )
}

pub fn plan_retry_prompt() -> String {
    let input = format!(
        "Não consegui interpretar a lista de features. Reenvie em '{FEATURES}' um ARRAY JSON\n\
válido, exatamente no formato {FEATURES_SHAPE} — só o array, sem texto ao redor.\n\
Repita o comando `{VERIFY_CMD}` e `{TARGET_DIR}`."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "plan",
            vec![
                FEATURES.to_string(),
                VERIFY_CMD.to_string(),
                TARGET_DIR.to_string(),
            ],
        ),
        None,
    )
}

// --- loop por feature (uma sessão de contexto fresco) --------------------------

pub fn bearings_prompt() -> String {
    let brief = brief_block();
    let input = format!(
        "=== NOVA SESSÃO (contexto limpo) ===\n\
Você é um agente de codificação começando uma sessão FRESCA. Não assuma nada da\n\
sessão anterior — todo o estado está nos artefatos persistentes.\n\
{brief}\
Oriente-se com saída curta: rode `pwd`, leia só o fim do `progress.txt` e o\n\
`git log --oneline` recente para entender o que já foi feito. Não cole logs\n\
longos; se precisar preservar detalhe, salve em `.harness/logs/`.\n\
\n\
Resuma o que encontrou em '{NOTE}' em 2-4 linhas."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "bearings", vec![NOTE.to_string()]),
        Some(&prompt_formatter::skills(&["dev-bearings"])),
    )
}

pub fn smoke_prompt() -> String {
    let target_dir = run_config_store::load().target_dir;
    let input = format!(
        "Smoke test: rode `./init.sh` no diretório-alvo ({target_dir}) e confirme\n\
que o baseline sobe/builda sem erro antes de mexer em qualquer feature. Salve a\n\
saída completa em `.harness/logs/smoke.log` e relate em '{SMOKE}' só `ok` ou o\n\
erro principal e o caminho do log."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "smoke", vec![SMOKE.to_string()]),
        Some(&prompt_formatter::skills(&["dev-smoke"])),
    )
}

pub fn pick_prompt() -> String {
    let input = "Baseline confirmado. Envie o comando `pick` para receber a próxima feature a\n\
implementar (a de maior prioridade ainda pendente — o harness escolhe).";

    prompt_formatter::format(
        input,
        &Envelope::new(envelope_type::COMMAND, "pick", vec![]),
        None,
    )
}

pub fn implement_prompt(feature: &Feature) -> String {
    let target_dir = run_config_store::load().target_dir;
    let brief = brief_block();
    let context = feature_context_block(feature);
    let input = format!(
        "Implemente EXCLUSIVAMENTE esta feature, de forma incremental e mínima — nada além\n\
dela:\n\
{brief}\
Feature #{} (prioridade {}): {}\n\
{context}\
Trabalhe no diretório-alvo ({target_dir}). Se rodar comandos com\n\
saída longa, salve em `.harness/logs/` e não cole logs no resumo. Ao terminar,\n\
resuma o que implementou em '{SUMMARY}' em uma frase curta.",
        feature.id, feature.priority, feature.title
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "implement",
            vec![SUMMARY.to_string()],
        ),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn verify_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "O harness não encontrou `verify-feature.sh` no diretório-alvo, então faça o\n\
self-verify manual da feature #{feature_id}\n\
({}) como um usuário faria: rode\n\
`{}` no diretório-alvo ({}) e\n\
confirme o comportamento ponta a ponta. Salve a saída completa em\n\
`.harness/logs/verify-{feature_id}.log`.\n\
\n\
Responda em '{RESULT}' começando com `PASS` ou `FAIL: <motivo>`, incluindo só o\n\
erro principal e o caminho do log.",
        state(CURRENT_FEATURE_TITLE_KEY),
        config.verify_cmd,
        config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "verify", vec![RESULT.to_string()]),
        Some(&prompt_formatter::skills(&["dev-verify"])),
    )
}

pub fn verify_retry_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "O veredito do self-verify não começou com `PASS` nem `FAIL`. Reexecute, se\n\
necessário, `{}` no diretório-alvo ({})\n\
salvando a saída completa em `.harness/logs/verify-{feature_id}.log`.\n\
Responda em '{RESULT}' começando exatamente com `PASS` ou `FAIL: <motivo>`,\n\
sem colar logs longos.",
        config.verify_cmd, config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "verify", vec![RESULT.to_string()]),
        Some(&prompt_formatter::skills(&["dev-verify"])),
    )
}

pub fn fix_prompt(verify_failure: Option<&str>) -> String {
    let failure = match verify_failure {
        Some(f) if !f.trim().is_empty() => format!("Falha observada: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "A verificação FALHOU na feature #{}\n\
({}). {failure}Corrija a implementação (ainda SÓ esta feature).\n\
Se consultar logs, leia só o trecho relevante. Resuma o ajuste em '{SUMMARY}' —\n\
em seguida verificamos de novo.",
        state(CURRENT_FEATURE_ID_KEY),
        state(CURRENT_FEATURE_TITLE_KEY)
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "implement",
            vec![SUMMARY.to_string()],
        ),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn handoff_prompt(automatic_failure: Option<&str>) -> String {
    let failure = match automatic_failure {
        Some(f) if !f.trim().is_empty() => format!("O handoff automatico falhou: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "{failure}Deixe o estado LIMPO para a próxima sessão:\n\
1. `git commit` com mensagem descritiva referenciando a feature #{}. Se o diretório-alvo não estiver em um repositório Git, registre isso explicitamente como `NO_GIT: <motivo>`.\n\
2. Anexe uma linha ao `progress.txt`: feature concluída, o que foi feito e como verificar.\n\
\n\
Confirme com o hash do commit ou `NO_GIT: <motivo>` em '{COMMIT}'.",
        state(CURRENT_FEATURE_ID_KEY)
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![COMMIT.to_string()]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}

pub fn handoff_retry_prompt() -> String {
    let target_dir = run_config_store::load().target_dir;
    let input = format!(
        "A confirmação do handoff veio vazia. Atualize `progress.txt` no diretório-alvo\n\
({target_dir}) e responda em '{COMMIT}' com o hash do commit ou\n\
`NO_GIT: <motivo>` quando não houver repositório Git."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![COMMIT.to_string()]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}
