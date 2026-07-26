//! Flow de desenvolvimento long-running (padrão "Effective harnesses for long-running
//! agents", Anthropic). Um inicializador (session 0) expande o brief numa lista
//! priorizada de features; depois um loop de sessões de contexto fresco implementa UMA
//! feature por vez:
//!
//!   start → plan → [bearings → smoke → pick → implement → verify(auto-handoff)]*
//!
//! O estado que atravessa os hard resets vive em artefatos persistentes: o
//! `feature_store` (feature_list.json, do harness) e o progress.txt + git (do
//! diretório-alvo). Cada task só faz efeitos e decide o PRÓXIMO comando (o envelope de
//! saída) — a orquestração (dispatch, guardas globais, transporte) fica em
//! `harness_engine`.

use harness_engine::Envelope;
use harness_engine::run_config_store::RunConfig;
use harness_engine::{docs_reader, feature_store, harness_config, run_config_store, state_store};

use crate::{handoff, prompts, verify};

// Guardas locais deste flow (o teto global do harness.json, 12, é curto demais p/ um
// loop). Poucas features + teto de passos POR feature: barra o loop implement↔verify que
// nunca fecha.
pub const MAX_FEATURES: usize = 10;
pub const STEPS_PER_FEATURE: i32 = 8;

// Teto de passos efetivo passado ao harness_host (override do global): folga p/ o pior
// caso de MAX_FEATURES features gastando STEPS_PER_FEATURE cada, mais o start/plan e as
// fronteiras.
pub const STEP_BUDGET: i32 = MAX_FEATURES as i32 * STEPS_PER_FEATURE + 8;

// Chaves do state_store::Data usadas por este módulo e por prompts.rs/handoff.rs — const
// em vez de string literal repetida, para que um typo em qualquer um dos arquivos vire
// erro de compilação em vez de uma chave nunca lida.
pub const CURRENT_FEATURE_ID_KEY: &str = "current_feature_id";
pub const CURRENT_FEATURE_TITLE_KEY: &str = "current_feature_title";
pub const CURRENT_FEATURE_SUMMARY_KEY: &str = "current_feature_summary";
pub const CURRENT_FEATURE_VERIFY_KEY: &str = "current_feature_verify";
pub const FEATURE_STEPS_KEY: &str = "feature_steps";

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

fn docs_folder() -> String {
    harness_config::current().docs_folder
}

pub fn start() -> String {
    // Uma sessão anterior (talvez de outro driver — os tokens acabaram numa IDE e outra
    // assume) pode ter morrido no meio de uma feature. Reiniciar jogaria fora trabalho em
    // andamento; retomar é seguro e determinístico: bearings é reentrante por construção
    // (só rearma a guarda por feature) e o próximo pick() reseleciona a mesma feature,
    // ainda pendente — sem precisar saber exatamente onde a sessão anterior parou.
    if feature_store::pending_count() > 0 {
        eprintln!(
            "[dev] run em andamento detectado (feature pendente); retomando via bearings em vez de resetar."
        );
        return prompts::bearings_prompt();
    }

    // Flow PRODUTOR da feature_list: novo run apaga a do run anterior.
    feature_store::reset();
    run_config_store::reset();

    // Brief (o que construir) vem de docs/ ou, sem docs, do modo interativo.
    let folder = docs_folder();
    if !docs_reader::has_docs(&folder) {
        return prompts::initializer_interactive();
    }

    let (content, files) = docs_reader::read(&folder);
    state_store::set("origem", "docs");
    prompts::initializer_prompt(&content, &files)
}

pub fn plan(envelope: Option<&Envelope>) -> String {
    let features = feature_store::parse(&arg(envelope));
    if features.is_empty() {
        return prompts::plan_retry_prompt(); // não interpretou → re-pede (loop corretivo)
    }

    // Teto de features: fica com as de maior prioridade (menor número).
    let mut sorted = features;
    sorted.sort_by_key(|f| (f.priority, f.id));
    let mut capped: Vec<_> = sorted.into_iter().take(MAX_FEATURES).collect();

    // Higieniza depends_on: uma feature sobrevivente pode depender de um id cortado
    // acima, o que a bloquearia para sempre (nunca "pronta") sem que o driver tenha como
    // saber — quem cortou foi o harness, não ele. Cortar nós de um grafo já acíclico
    // (validado em feature_store::parse) não pode criar ciclo, então só a limpeza de
    // dangling é necessária.
    let capped_ids: std::collections::HashSet<i32> = capped.iter().map(|f| f.id).collect();
    for f in &mut capped {
        f.depends_on.retain(|d| capped_ids.contains(d));
    }

    feature_store::write(&capped);

    // Comando de verificação, diretório-alvo e identidade do run: reidratados a cada passo
    // de smoke/verify. Fora de state.json de propósito - ver run_config_store. run_id nasce
    // aqui (mesmo instante em que start() decidiu que este é um run novo, não retomado) e
    // sobrevive a toda sessão seguinte sem precisar aparecer no Envelope trocado com o
    // modelo (RFC §6.4 — identidade do run é concern do control plane, não do contrato).
    run_config_store::write(&RunConfig {
        verify_cmd: arg_at(envelope, 1, "dotnet test"),
        target_dir: arg_at(envelope, 2, "."),
        run_id: uuid::Uuid::new_v4().to_string(),
    });

    prompts::bearings_prompt()
}

pub fn bearings(_envelope: Option<&Envelope>) -> String {
    // Nova sessão (uma feature): zera o contador da guarda por feature.
    state_store::set(FEATURE_STEPS_KEY, "1");
    prompts::smoke_prompt()
}

pub fn smoke(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        stop("guarda por feature")
    } else {
        prompts::pick_prompt()
    }
}

pub fn pick(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("guarda por feature");
    }

    // Seleção DETERMINÍSTICA: maior prioridade entre as prontas (dependências
    // satisfeitas). O harness escolhe, não o LLM.
    let next = match feature_store::next_pending() {
        Some(f) => f,
        None => {
            // pending_count() == 0 é o caso normal (handoff já teria fechado antes).
            // Pendência > 0 só é alcançável por um feature_list.json editado à mão fora
            // do grafo validado no plan (write/mark_passed não revalidam) — não finge
            // sucesso nesse caso.
            return if feature_store::pending_count() == 0 {
                done()
            } else {
                stop("dependências bloqueadas — nenhuma feature pendente está pronta")
            };
        }
    };

    state_store::set(CURRENT_FEATURE_ID_KEY, &next.id.to_string());
    state_store::set(CURRENT_FEATURE_TITLE_KEY, &next.title);
    // Etiqueta o trace com a feature corrente (ver trace::TraceEntry::label) — sem isso,
    // cada linha do trace.jsonl só tem o step global, sem dizer a qual feature ele pertence.
    state_store::set(
        state_store::TRACE_LABEL_KEY,
        &format!("feature:{}", next.id),
    );
    prompts::implement_prompt(&next)
}

pub fn implement(envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("guarda por feature");
    }

    let summary = arg(envelope).trim().to_string();
    if !summary.is_empty() {
        state_store::set(CURRENT_FEATURE_SUMMARY_KEY, &summary);
    }

    let feature_id: Option<i32> = state(CURRENT_FEATURE_ID_KEY).parse().ok();
    if let Some(feature_id) = feature_id {
        // target_dir inválido (raiz, home, instalação do harness) -> mesmo caminho de
        // "não tentou verificação automática" que target_dir sem verify-feature.sh.
        if let Ok(target_dir) = handoff::resolve_target_dir(&run_config_store::load().target_dir) {
            let auto = verify::try_automated_verify(feature_id, &target_dir);
            if auto.attempted {
                state_store::set(CURRENT_FEATURE_VERIFY_KEY, &auto.result);
                return if auto.success {
                    handoff::complete_verified_feature(&auto.result)
                } else {
                    prompts::fix_prompt(Some(&auto.result))
                };
            }
        }
    }

    prompts::verify_prompt()
}

pub fn verify(envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("guarda por feature");
    }

    // FALHOU → volta a implementar a MESMA feature (loop de correção, limitado pela
    // guarda). PASSOU → o harness faz o handoff determinístico (progress + git) sem
    // gastar um turno do modelo; se falhar, cai no prompt legado de reparo manual.
    let result = arg(envelope).trim().to_string();
    let upper = result.to_uppercase();
    if upper.starts_with("FAIL") {
        return prompts::fix_prompt(Some(&result));
    }

    if upper.starts_with("PASS") {
        state_store::set(CURRENT_FEATURE_VERIFY_KEY, &result);
        return handoff::complete_verified_feature(&result);
    }

    prompts::verify_retry_prompt()
}

pub fn handoff_task(envelope: Option<&Envelope>) -> String {
    if arg(envelope).trim().is_empty() {
        return prompts::handoff_retry_prompt();
    }

    if let Ok(id) = state(CURRENT_FEATURE_ID_KEY).parse::<i32>() {
        feature_store::mark_passed(id);
    }

    // Alguma feature ainda pendente? Sim → próxima sessão (bearings). Não → fim.
    if feature_store::all_passing() {
        done()
    } else {
        prompts::bearings_prompt()
    }
}

// --- guardas e término -------------------------------------------------

/// Incrementa o contador da sessão e sinaliza estouro do teto por feature.
fn over_feature_budget() -> bool {
    let steps: i32 = state(FEATURE_STEPS_KEY).parse().unwrap_or(0) + 1;
    state_store::set(FEATURE_STEPS_KEY, &steps.to_string());

    if steps > STEPS_PER_FEATURE {
        eprintln!(
            "[dev] feature '{}' excedeu {STEPS_PER_FEATURE} passos; encerrando.",
            state(CURRENT_FEATURE_TITLE_KEY)
        );
        return true;
    }
    false
}

pub(crate) fn stop(motivo: &str) -> String {
    eprintln!("[dev] encerrado por {motivo}. feature_list em .harness/feature_list.json");
    "stop".to_string()
}

pub(crate) fn done() -> String {
    eprintln!(
        "[dev] todas as {} features passam; concluído. Estado em .harness/feature_list.json",
        feature_store::load().len()
    );
    "stop".to_string()
}

fn arg(envelope: Option<&Envelope>) -> String {
    envelope
        .and_then(|e| e.args.first())
        .cloned()
        .unwrap_or_default()
}

fn arg_at(envelope: Option<&Envelope>, index: usize, fallback: &str) -> String {
    match envelope.and_then(|e| e.args.get(index)) {
        Some(v) if !v.trim().is_empty() => v.clone(),
        _ => fallback.to_string(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use harness_engine::envelope::envelope_type;
    use harness_engine::feature_store::Feature;
    use harness_engine::task_registry::Action;
    use harness_engine::trace;
    use std::collections::HashMap;
    use std::sync::Mutex;

    // `current_dir` é global ao processo — serializa os testes deste crate que o mudam
    // (mesmo padrão do `test_support::lock_cwd` do harness_engine, mas local: crates
    // diferentes rodam em binários de teste — logo processos — diferentes).
    static CWD_GUARD: Mutex<()> = Mutex::new(());

    struct Isolated {
        _dir: tempfile::TempDir,
        previous: std::path::PathBuf,
    }

    impl Isolated {
        fn new() -> Self {
            let dir = tempfile::tempdir().unwrap();
            let previous = std::env::current_dir().unwrap();
            std::env::set_current_dir(dir.path()).unwrap();
            Self {
                _dir: dir,
                previous,
            }
        }
    }

    impl Drop for Isolated {
        fn drop(&mut self) {
            let _ = std::env::set_current_dir(&self.previous);
        }
    }

    fn lock_cwd() -> std::sync::MutexGuard<'static, ()> {
        CWD_GUARD.lock().unwrap_or_else(|p| p.into_inner())
    }

    const FEATURES_JSON: &str =
        r#"[{"id":1,"title":"A","priority":2},{"id":2,"title":"B","priority":1}]"#;

    fn cmd(value: &str, args: Vec<&str>) -> Envelope {
        Envelope::new(
            envelope_type::COMMAND,
            value,
            args.into_iter().map(|s| s.to_string()).collect(),
        )
    }

    fn plan_default() -> String {
        plan(Some(&cmd(
            "plan",
            vec![FEATURES_JSON, "dotnet test", "src/app"],
        )))
    }

    /// Leva o flow até deixar uma feature escolhida e implementada (pronta p/ verify),
    /// sem `verify-feature.sh` no target (cai no self-verify manual).
    fn advance_to_verify() {
        plan_default();
        bearings(Some(&cmd("bearings", vec!["orientado"])));
        smoke(Some(&cmd("smoke", vec!["baseline ok"])));
        pick(Some(&cmd("pick", vec![])));
        implement(Some(&cmd("implement", vec!["implementei"])));
    }

    fn write_verify_feature_script(target_dir: &std::path::Path, body: &str) {
        std::fs::create_dir_all(target_dir).unwrap();
        std::fs::write(target_dir.join("verify-feature.sh"), body).unwrap();
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            let mut perms = std::fs::metadata(target_dir.join("verify-feature.sh"))
                .unwrap()
                .permissions();
            perms.set_mode(0o755);
            std::fs::set_permissions(target_dir.join("verify-feature.sh"), perms).unwrap();
        }
    }

    #[test]
    fn start_sem_feature_pendente_reseta_feature_list_e_run_config() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        for f in feature_store::load() {
            feature_store::mark_passed(f.id);
        }
        assert!(!feature_store::load().is_empty());

        start();

        assert!(feature_store::load().is_empty());
        assert_eq!(run_config_store::load(), RunConfig::default());
    }

    #[test]
    fn start_com_feature_pendente_retoma_via_bearings_em_vez_de_resetar() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify(); // sessão "morre" antes do verify

        let result = start();

        assert!(result.contains("NOVA SESSÃO"));
        assert_eq!(feature_store::load().len(), 2);
        assert_eq!(feature_store::pending_count(), 2);
        assert_eq!(run_config_store::load().verify_cmd, "dotnet test");
        assert_eq!(run_config_store::load().target_dir, "src/app");
    }

    #[test]
    fn start_com_feature_pendente_preserva_o_run_id_do_plan_anterior() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify(); // sessão "morre" antes do verify
        let run_id_antes_do_start = run_config_store::load().run_id;
        assert!(!run_id_antes_do_start.is_empty());

        start();

        // Retomada não gera um novo run - a identidade do run tem que sobreviver ao "start".
        assert_eq!(run_config_store::load().run_id, run_id_antes_do_start);
    }

    #[test]
    fn plan_persiste_features_e_roteia_para_bearings() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = plan(Some(&cmd("plan", vec![FEATURES_JSON, "npm test", "web"])));

        assert_eq!(feature_store::load().len(), 2);
        assert_eq!(run_config_store::load().verify_cmd, "npm test");
        assert_eq!(run_config_store::load().target_dir, "web");
        assert!(result.contains("NOVA SESSÃO"));
        assert!(result.contains(r#""value":"bearings"#));
    }

    #[test]
    fn plan_gera_um_run_id_novo_e_nao_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan(Some(&cmd("plan", vec![FEATURES_JSON, "npm test", "web"])));

        let run_id = run_config_store::load().run_id;

        assert!(!run_id.is_empty());
        assert!(uuid::Uuid::parse_str(&run_id).is_ok());
    }

    #[test]
    fn plan_features_invalidas_reemite_o_plano() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = plan(Some(&cmd("plan", vec!["não é json", "dotnet test", "."])));

        assert!(feature_store::load().is_empty());
        assert_eq!(run_config_store::load(), RunConfig::default());
        assert!(result.contains(r#""value":"plan"#));
        assert!(!result.contains("NOVA SESSÃO"));
    }

    #[test]
    fn plan_depends_on_ciclico_reemite_o_plano() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let json = r#"[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]"#;
        let result = plan(Some(&cmd("plan", vec![json, "dotnet test", "."])));

        assert!(feature_store::load().is_empty());
        assert!(result.contains(r#""value":"plan"#));
        assert!(!result.contains("NOVA SESSÃO"));
    }

    #[test]
    fn plan_corte_max_features_remove_dependencia_para_id_cortado() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // id 1 (prioridade 1, a melhor) sobrevive ao corte; depende do id 2, cuja
        // prioridade (1000) é a pior de todas — garantidamente cortado pelo corte em
        // MAX_FEATURES. Os "extras" preenchem as vagas restantes.
        let extras: String = (3..3 + MAX_FEATURES - 1)
            .map(|i| format!(r#"{{"id":{i},"title":"extra{i}","priority":{i}}}"#))
            .collect::<Vec<_>>()
            .join(",");
        let json = format!(
            r#"[{{"id":1,"title":"sobrevivente","priority":1,"dependsOn":[2]}},{{"id":2,"title":"cortada","priority":1000}},{extras}]"#
        );

        plan(Some(&cmd("plan", vec![&json, "dotnet test", "."])));

        assert!(!feature_store::load().iter().any(|f| f.id == 2));
        let survivor = feature_store::load()
            .into_iter()
            .find(|f| f.id == 1)
            .unwrap();
        assert!(!survivor.depends_on.contains(&2));
    }

    #[test]
    fn pick_escolhe_maior_prioridade_e_grava_a_feature_corrente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        bearings(Some(&cmd("bearings", vec!["ok"])));
        let after_smoke = smoke(Some(&cmd("smoke", vec!["ok"])));
        assert!(after_smoke.contains(r#""value":"pick"#));

        let implement_prompt = pick(Some(&cmd("pick", vec![])));

        assert_eq!(
            state_store::get(CURRENT_FEATURE_ID_KEY),
            Some("2".to_string())
        ); // prioridade 1 = id 2 ("B")
        assert!(implement_prompt.contains('B'));
        assert!(implement_prompt.contains(r#""value":"implement"#));
    }

    #[test]
    fn pick_respeita_dependencia_escolhe_dependencia_antes_da_dependente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let json = r#"[{"id":1,"title":"fundação","priority":2},{"id":2,"title":"depende","priority":1,"dependsOn":[1]}]"#;
        plan(Some(&cmd("plan", vec![json, "dotnet test", "."])));
        bearings(Some(&cmd("bearings", vec!["ok"])));
        smoke(Some(&cmd("smoke", vec!["ok"])));

        pick(Some(&cmd("pick", vec![])));

        assert_eq!(
            state_store::get(CURRENT_FEATURE_ID_KEY),
            Some("1".to_string())
        );
    }

    #[test]
    fn pick_sem_feature_pronta_mas_com_pendencia_encerra_sem_reportar_concluido() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default(); // popula run_config; a lista será sobrescrita a seguir
        feature_store::write(&[
            Feature {
                id: 1,
                title: "A".to_string(),
                priority: 1,
                passes: false,
                depends_on: vec![2],
            },
            Feature {
                id: 2,
                title: "B".to_string(),
                priority: 2,
                passes: false,
                depends_on: vec![1],
            },
        ]);
        bearings(Some(&cmd("bearings", vec!["ok"])));
        smoke(Some(&cmd("smoke", vec!["ok"])));

        let result = pick(Some(&cmd("pick", vec![])));

        assert_eq!(result, "stop");
        assert_eq!(feature_store::pending_count(), 2);
    }

    #[test]
    fn verify_fail_volta_para_implement() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = verify(Some(&cmd("verify", vec!["FAIL: testes vermelhos"])));

        assert!(result.contains("FALHOU"));
        assert!(result.contains(r#""value":"implement"#));
    }

    #[test]
    fn verify_pass_executa_handoff_automatico_e_avanca() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = verify(Some(&cmd("verify", vec!["PASS"])));

        assert!(result.contains("NOVA SESSÃO"));
        assert!(!result.contains(r#""value":"handoff"#));
        assert_eq!(feature_store::pending_count(), 1);
        assert!(
            std::fs::read_to_string("src/app/progress.txt")
                .unwrap()
                .contains("Feature #2")
        );
    }

    #[test]
    fn verify_veredito_invalido_reemite_verify() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = verify(Some(&cmd("verify", vec!["rodei os testes e passou"])));

        assert!(result.contains(r#""value":"verify"#));
        assert!(!result.contains(r#""value":"handoff"#));
        assert!(result.contains("não começou"));
    }

    #[test]
    fn implement_com_verify_feature_passando_executa_verify_e_handoff_automaticos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write_verify_feature_script(
            std::path::Path::new("src/app"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n",
        );
        plan_default();
        bearings(Some(&cmd("bearings", vec!["orientado"])));
        smoke(Some(&cmd("smoke", vec!["baseline ok"])));
        pick(Some(&cmd("pick", vec![])));

        let result = implement(Some(&cmd("implement", vec!["implementei"])));

        assert!(result.contains("NOVA SESSÃO"));
        assert!(!result.contains(r#""value":"verify"#));
        assert_eq!(feature_store::pending_count(), 1);
        let progress = std::fs::read_to_string("src/app/progress.txt").unwrap();
        assert!(progress.contains("Feature #2"));
        assert!(progress.contains("PASS: feature 2 verificada"));
        assert!(progress.contains(".harness/logs/verify-feature-2.log"));
    }

    #[test]
    fn implement_com_verify_feature_falhando_volta_para_fix() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        write_verify_feature_script(
            std::path::Path::new("src/app"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"FAIL: feature $1 quebrou\"\necho \"LINHA DETALHADA QUE FICA SO NO LOG\"\nexit 7\n",
        );
        plan_default();
        bearings(Some(&cmd("bearings", vec!["orientado"])));
        smoke(Some(&cmd("smoke", vec!["baseline ok"])));
        pick(Some(&cmd("pick", vec![])));

        let result = implement(Some(&cmd("implement", vec!["implementei"])));

        assert!(result.contains("FALHOU"));
        assert!(result.contains("feature 2 quebrou"));
        assert!(result.contains(".harness/logs/verify-feature-2.log"));
        assert!(!result.contains("LINHA DETALHADA QUE FICA SO NO LOG"));
        assert!(result.contains(r#""value":"implement"#));
        assert_eq!(feature_store::pending_count(), 2);
        assert!(!std::path::Path::new("src/app/progress.txt").exists());
    }

    #[test]
    fn handoff_vazio_reemite_handoff_e_nao_marca_feature_como_passando() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = handoff_task(Some(&cmd("handoff", vec![""])));

        assert!(result.contains(r#""value":"handoff"#));
        assert_eq!(feature_store::pending_count(), 2);
    }

    #[test]
    fn handoff_legado_com_hash_marca_feature_como_passando() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = handoff_task(Some(&cmd("handoff", vec!["abc123"])));

        assert!(result.contains("NOVA SESSÃO"));
        assert_eq!(feature_store::pending_count(), 1);
    }

    #[test]
    fn guarda_por_feature_ao_exceder_o_teto_encerra() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        bearings(Some(&cmd("bearings", vec!["ok"]))); // zera para 1
        state_store::set(FEATURE_STEPS_KEY, &STEPS_PER_FEATURE.to_string()); // no limite

        let result = smoke(Some(&cmd("smoke", vec!["ok"]))); // próximo bump ultrapassa

        assert_eq!(result, "stop");
    }

    #[test]
    fn dispatch_start_sem_feature_pendente_trunca_trace_e_step() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        for f in feature_store::load() {
            feature_store::mark_passed(f.id);
        }
        trace::append(41, "handoff", trace::trace_outcome::INSTRUCTION, 10);

        let should_reset: &dyn Fn() -> bool = &|| feature_store::pending_count() == 0;
        harness_engine::task_registry::dispatch(
            &[r#"{"type":"text","value":"start"}"#.to_string()],
            &{
                let mut m: HashMap<String, Action> = HashMap::new();
                m.insert("start".to_string(), std::sync::Arc::new(|_| start()));
                m
            },
            None,
            None,
            Some(should_reset),
        );

        assert!(trace::load().iter().all(|e| e.step != 41));
        assert_eq!(state_store::load().step, 1);
    }
}
