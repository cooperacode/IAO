//! Padrão "long-running agent": inicializador + loop de sessões frescas, uma feature por
//! vez. Nenhuma orquestração aqui — dispatch, guardas e transporte vivem em
//! `harness_engine`.
//!
//!   start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*

mod handoff;
mod prompts;
mod tasks;
mod verify;

use std::collections::HashMap;
use std::sync::Arc;

use harness_engine::envelope_validation;
use harness_engine::feature_store;
use harness_engine::harness_host;
use harness_engine::task_registry::Action;

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();

    let mut tasks: HashMap<String, Action> = HashMap::new();
    tasks.insert("start".to_string(), Arc::new(|_| tasks::start()));
    tasks.insert("plan".to_string(), Arc::new(tasks::plan));
    tasks.insert("bearings".to_string(), Arc::new(tasks::bearings));
    tasks.insert("smoke".to_string(), Arc::new(tasks::smoke));
    tasks.insert("pick".to_string(), Arc::new(tasks::pick));
    tasks.insert("implement".to_string(), Arc::new(tasks::implement));
    tasks.insert("verify".to_string(), Arc::new(tasks::verify));
    tasks.insert("handoff".to_string(), Arc::new(tasks::handoff_task));

    // Expectativa contextual por comando; recusa vira erro corretivo (o driver corrige e
    // reenvia). `pick` não tem validador — não carrega artefato do driver (a seleção é
    // do harness).
    let mut validators: HashMap<String, envelope_validation::Validator> = HashMap::new();
    validators.insert(
        "plan".to_string(),
        envelope_validation::not_empty("o array JSON de features [{id,title,priority}]"),
    );
    validators.insert(
        "bearings".to_string(),
        envelope_validation::not_empty("o resumo curto da orientação (pwd, progress, git log)"),
    );
    validators.insert(
        "smoke".to_string(),
        envelope_validation::not_empty(
            "o resultado compacto do smoke test (init.sh + caminho do log)",
        ),
    );
    validators.insert(
        "implement".to_string(),
        envelope_validation::not_empty("o resumo curto do que foi implementado"),
    );
    validators.insert(
        "verify".to_string(),
        envelope_validation::matches(
            r"^(PASS\b|FAIL\b)",
            "o veredito compacto do self-verify começando com PASS ou FAIL: motivo",
        ),
    );
    validators.insert(
        "handoff".to_string(),
        envelope_validation::matches(
            r"^([0-9a-f]{6,40}\b|NO_GIT:\s+\S.*)$",
            "o hash do commit ou NO_GIT: motivo quando nao houver repositorio Git",
        ),
    );

    // Snapshots próprios: se este flow dividir o `.harness/` com outros flows (mesmo
    // workspace), ele NÃO pode sobrescrever o last-run.* que outro flow consome. Congela
    // no seu próprio caminho.
    // max_steps: override do teto global (12) — este flow é long-running e precisa de
    // folga p/ o loop.
    // should_reset_on_start: um "start" também chega no hard reset por feature (sessão
    // fresca que reabre um run em andamento) — só é run novo de verdade quando não há
    // feature pendente.
    let should_reset_on_start: &dyn Fn() -> bool = &|| feature_store::pending_count() == 0;

    let code = harness_host::run(
        &args,
        &tasks,
        ".harness/last-development.trace.jsonl",
        ".harness/last-development.state.json",
        Some(&validators),
        Some(tasks::STEP_BUDGET),
        Some(should_reset_on_start),
    );

    std::process::exit(code);
}
