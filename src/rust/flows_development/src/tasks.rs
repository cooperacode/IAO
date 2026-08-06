//! Long-running development flow ("Effective harnesses for long-running agents" pattern,
//! Anthropic). An initializer (session 0) expands the brief into a prioritized feature
//! list; then a loop of fresh-context sessions implements ONE feature at a time:
//!
//!   start → plan → [implement → verify(auto-handoff)]*
//!
//! The state that survives the hard resets lives in persistent artifacts: the
//! `feature_store` (feature_list.json, the harness's) and progress.txt + git (the target
//! directory's). Each task only performs effects and decides the NEXT command (the output
//! envelope) — orchestration (dispatch, global guards, transport) lives in
//! `harness_engine`.

use harness_engine::Envelope;
use harness_engine::run_config_store::RunConfig;
use harness_engine::{
    artifact_store, docs_reader, feature_store, harness_config, harness_log, run_config_store,
    state_store,
};

use crate::{handoff, prompts, verify};
use std::process::Command;

// Local guards for this flow (harness.json's global ceiling, 12, is too short for a
// loop). Few features + a per-feature step ceiling: bars the implement↔verify loop that
// never closes.
pub const MAX_FEATURES: usize = 10;
pub const STEPS_PER_FEATURE: i32 = 8;

// Effective step ceiling passed to harness_host (override of the global one): slack for
// the worst case of MAX_FEATURES features spending STEPS_PER_FEATURE each, plus
// start/plan and the boundaries.
pub const STEP_BUDGET: i32 = MAX_FEATURES as i32 * STEPS_PER_FEATURE + 8;

// state_store::Data keys used by this module and by prompts.rs/handoff.rs — a const
// instead of a repeated string literal, so a typo in any of these files becomes a
// compile error instead of a key that's never read.
pub const CURRENT_FEATURE_ID_KEY: &str = "current_feature_id";
pub const CURRENT_FEATURE_TITLE_KEY: &str = "current_feature_title";
pub const CURRENT_FEATURE_SUMMARY_KEY: &str = "current_feature_summary";
pub const CURRENT_FEATURE_VERIFY_KEY: &str = "current_feature_verify";
pub const CURRENT_BEARINGS_KEY: &str = "current_bearings";
pub const FEATURE_STEPS_KEY: &str = "feature_steps";

// Name of the brief artifact in artifact_store (.harness/brief.md) — retained for
// auditability and compatibility; implementation sessions use each feature's bounded context.
pub const BRIEF_ARTIFACT_NAME: &str = "brief";

// Where the driver writes the raw (unescaped) feature-list JSON array with its file-write
// tool. Requiring it inline in the envelope's args would force the driver to serialize and
// escape a large JSON document as a single string value inside another single-line JSON
// object — a format-compliance task large drivers have been observed to fail at, falling
// back to echoing the placeholder token itself.
pub const PLAN_FILE_PATH: &str = ".harness/plan.json";

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

fn docs_folder() -> String {
    harness_config::current().docs_folder
}

pub fn start() -> String {
    // A previous session (maybe from another driver — tokens ran out in one IDE and
    // another takes over) may have died mid-feature. Restarting would throw away work in
    // progress; resuming is safe and deterministic: bearings is reentrant by construction
    // (it only rearms the per-feature guard) and the next pick() reselects the same
    // feature, still pending — without needing to know exactly where the previous
    // session stopped.
    if feature_store::pending_count() > 0 {
        harness_log::info(
            "[dev] run in progress detected (pending feature); resuming via bearings instead of resetting.",
        );
        return bearings(None);
    }

    // Flow that PRODUCES feature_list: a new run erases the previous run's.
    feature_store::reset();
    run_config_store::reset();
    // Without this, a new run in interactive mode (no specs/) would silently inherit the
    // brief.md from a previous run — interactive mode never calls artifact_store::write,
    // so only this reset guarantees no brief from an old topic survives.
    artifact_store::reset();
    // A stale plan.json from a previous (or aborted) run must not satisfy this one before
    // the driver writes a fresh array — best-effort, absence is not an error.
    let _ = std::fs::remove_file(PLAN_FILE_PATH);

    // Brief (what to build) comes from specs/ or, without specs, from interactive mode.
    let folder = docs_folder();
    if !docs_reader::has_docs(&folder) {
        return prompts::initializer_interactive();
    }

    let (content, files) = docs_reader::read(&folder);
    // Persisted for auditability and compatibility; implementation sessions use the bounded
    // context copied into each feature by the planner.
    artifact_store::write(BRIEF_ARTIFACT_NAME, &content);
    state_store::set("origem", "specs");
    prompts::initializer_prompt(&content, &files)
}

// plan interprets the driver's feature array (written to PLAN_FILE_PATH, not the
// envelope — see the comment on PLAN_FILE_PATH) and persists the run configuration.
pub fn plan(envelope: Option<&Envelope>) -> String {
    let features = feature_store::parse(&read_plan_file());
    if features.is_empty() {
        return prompts::plan_retry_prompt(); // couldn't parse → re-request (corrective loop)
    }

    // Feature ceiling: keeps the highest-priority ones (lowest number).
    let mut sorted = features;
    sorted.sort_by_key(|f| (f.priority, f.id));
    let mut capped: Vec<_> = sorted.into_iter().take(MAX_FEATURES).collect();

    // Sanitize depends_on: a surviving feature may depend on an id cut above, which would
    // block it forever (never "ready") with no way for the driver to know — the harness
    // did the cutting, not it. Cutting nodes from an already-acyclic graph (validated in
    // feature_store::parse) can't create a cycle, so only cleaning up dangling references
    // is needed.
    let capped_ids: std::collections::HashSet<i32> = capped.iter().map(|f| f.id).collect();
    for f in &mut capped {
        f.depends_on.retain(|d| capped_ids.contains(d));
    }

    feature_store::write(&capped);

    // Verify command, target directory, and run identity: rehydrated on every
    // smoke/verify step. Kept out of state.json on purpose — see run_config_store. run_id
    // is born here (the same instant start() decided this is a new run, not a resumed
    // one) and survives every subsequent session without needing to appear in the
    // Envelope exchanged with the model (RFC §6.4 — run identity is a control-plane
    // concern, not part of the contract).
    run_config_store::write(&RunConfig {
        verify_cmd: std::env::var("HARNESS_VERIFY_CMD").ok().filter(|v| !v.trim().is_empty()).unwrap_or_else(|| arg_at(envelope, 0, "dotnet test")),
        target_dir: std::env::var("HARNESS_TARGET_DIR").ok().filter(|v| !v.trim().is_empty()).unwrap_or_else(|| arg_at(envelope, 1, ".")),
        run_id: uuid::Uuid::new_v4().to_string(),
    });

    // Bearings, smoke, and pick are deterministic harness work. Keep them inside the
    // same dispatch so the first driver turn after planning is the creative implementation
    // turn, matching the .NET flow.
    bearings(None)
}

pub fn bearings(_envelope: Option<&Envelope>) -> String {
    // New session (one feature): resets the per-feature guard counter.
    state_store::set(FEATURE_STEPS_KEY, "1");
    capture_bearings();
    smoke(None)
}

pub fn smoke(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        stop("per-feature guard")
    } else if let Err(failure) = run_smoke() {
        prompts::smoke_fix_prompt(&failure)
    } else {
        // Selection is deterministic and does not need a driver acknowledgement.
        pick(None)
    }
}

pub fn pick(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("per-feature guard");
    }

    // DETERMINISTIC selection: highest priority among the ready ones (dependencies
    // satisfied). The harness chooses, not the LLM.
    let next = match feature_store::next_pending() {
        Some(f) => f,
        None => {
            // pending_count() == 0 is the normal case (handoff would already have closed
            // it before). A pending count > 0 is only reachable via a feature_list.json
            // hand-edited outside the graph validated in plan (write/mark_passed don't
            // revalidate) — doesn't fake success in that case.
            return if feature_store::pending_count() == 0 {
                done()
            } else {
                stop("blocked dependencies — no pending feature is ready")
            };
        }
    };

    state_store::set(CURRENT_FEATURE_ID_KEY, &next.id.to_string());
    state_store::set(CURRENT_FEATURE_TITLE_KEY, &next.title);
    // Tags the trace with the current feature (see trace::TraceEntry::label) — without
    // this, every trace.jsonl line only has the global step, without saying which
    // feature it belongs to.
    state_store::set(
        state_store::TRACE_LABEL_KEY,
        &format!("feature:{}", next.id),
    );
    prompts::implement_prompt(&next)
}

pub fn implement(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("per-feature guard");
    }

    state_store::set(CURRENT_FEATURE_SUMMARY_KEY, &implementation_summary());

    let feature_id: Option<i32> = state(CURRENT_FEATURE_ID_KEY).parse().ok();
    if let Some(feature_id) = feature_id {
        // Invalid target_dir (root, home, harness install) -> same "automatic
        // verification not attempted" path as a target_dir with no verify-feature.sh.
        if let Ok(target_dir) = handoff::resolve_target_dir(&run_config_store::load().target_dir) {
            let config = run_config_store::load();
            let auto = verify::try_automated_verify(feature_id, &target_dir, &config.verify_cmd);
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

pub fn verify(_envelope: Option<&Envelope>) -> String {
    if over_feature_budget() {
        return stop("per-feature guard");
    }

    let config = run_config_store::load();
    let id = match state(CURRENT_FEATURE_ID_KEY).parse::<i32>() { Ok(v) => v, Err(_) => return prompts::verify_retry_prompt() };
    let target = match handoff::resolve_target_dir(&config.target_dir) { Ok(v) => v, Err(_) => return prompts::verify_retry_prompt() };
    let auto = verify::try_automated_verify(id, &target, &config.verify_cmd);
    if !auto.attempted { return prompts::verify_retry_prompt(); }
    state_store::set(CURRENT_FEATURE_VERIFY_KEY, &auto.result);
    if auto.success { handoff::complete_verified_feature(&auto.result) } else { prompts::fix_prompt(Some(&auto.result)) }
}

pub fn handoff_task(_envelope: Option<&Envelope>) -> String {
    let result = state(CURRENT_FEATURE_VERIFY_KEY);
    if !result.to_uppercase().starts_with("PASS") { return prompts::verify_retry_prompt(); }
    handoff::complete_verified_feature(&result)
}

fn capture_bearings() {
    if let Ok(target) = handoff::resolve_target_dir(&run_config_store::load().target_dir) {
        let progress = std::fs::read_to_string(target.join("progress.txt")).unwrap_or_default();
        let tail = progress.lines().rev().take(12).collect::<Vec<_>>().into_iter().rev().collect::<Vec<_>>().join("\n");
        let log = harness_engine::git_command::run(&target, &["log", "-n", "10", "--oneline"]);
        let evidence = format!("cwd: {}\nprogress tail:\n{}\ngit log:\n{}", target.display(), tail, handoff::one_line(&log.output, "no git history"));
        state_store::set(CURRENT_BEARINGS_KEY, &evidence.chars().take(4000).collect::<String>());
    }
}

fn run_smoke() -> Result<(), String> {
    let target = handoff::resolve_target_dir(&run_config_store::load().target_dir)?;
    let script = target.join("init.sh");
    if !script.is_file() { return Err("init.sh is missing from the target directory".to_string()); }
    let output = Command::new("bash").arg(&script).current_dir(&target).output().map_err(|e| e.to_string())?;
    let log = std::path::PathBuf::from(".harness/logs/smoke.log");
    if let Some(parent) = log.parent() { let _ = std::fs::create_dir_all(parent); }
    let _ = std::fs::write(&log, format!("exitCode: {}\n\n--- stdout ---\n{}\n\n--- stderr ---\n{}\n", output.status.code().unwrap_or(-1), String::from_utf8_lossy(&output.stdout), String::from_utf8_lossy(&output.stderr)));
    if output.status.success() { Ok(()) } else { Err("init.sh failed. Log: .harness/logs/smoke.log".to_string()) }
}

fn implementation_summary() -> String {
    let config = run_config_store::load();
    if let Ok(target) = handoff::resolve_target_dir(&config.target_dir) {
        let diff = harness_engine::git_command::run(&target, &["diff", "HEAD", "--stat", ".", ":(exclude).harness"]);
        if diff.exit_code == 0 && !diff.output.trim().is_empty() { return handoff::one_line(&diff.output, "implementation completed"); }
        let status = harness_engine::git_command::run(&target, &["status", "--short", "--", ".", ":(exclude).harness"]);
        return handoff::one_line(&status.output, "implementation completed");
    }
    "implementation completed".to_string()
}

// --- guards and termination -------------------------------------------------

/// Increments the session counter and signals a per-feature ceiling overrun.
fn over_feature_budget() -> bool {
    let steps: i32 = state(FEATURE_STEPS_KEY).parse().unwrap_or(0) + 1;
    state_store::set(FEATURE_STEPS_KEY, &steps.to_string());

    if steps > STEPS_PER_FEATURE {
        harness_log::error(&format!(
            "[dev] feature '{}' exceeded {STEPS_PER_FEATURE} steps; stopping.",
            state(CURRENT_FEATURE_TITLE_KEY)
        ));
        return true;
    }
    false
}

pub(crate) fn stop(reason: &str) -> String {
    harness_log::error(&format!("[dev] stopped due to {reason}. feature_list in .harness/feature_list.json"));
    "stop".to_string()
}

pub(crate) fn done() -> String {
    harness_log::info(&format!(
        "[dev] all {} features pass; done. State in .harness/feature_list.json",
        feature_store::load().len()
    ));
    "stop".to_string()
}

/// Reads the driver-written feature array from PLAN_FILE_PATH. Empty string if
/// absent/unreadable — plan() treats that the same as an unparseable array (retry).
fn read_plan_file() -> String {
    std::fs::read_to_string(PLAN_FILE_PATH).unwrap_or_default()
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

    // `current_dir` is global to the process — serializes this crate's tests that change
    // it (same pattern as harness_engine's `test_support::lock_cwd`, but local: different
    // crates run in different test binaries — hence different processes).
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

    /// Writes the driver-side feature array to PLAN_FILE_PATH — plan() reads features from
    /// that file, not from the envelope's args (see PLAN_FILE_PATH above).
    fn write_plan_file(features: &str) {
        std::fs::create_dir_all(
            std::path::Path::new(PLAN_FILE_PATH)
                .parent()
                .unwrap(),
        )
        .unwrap();
        std::fs::write(PLAN_FILE_PATH, features).unwrap();
    }

    fn plan_cmd(features: &str, verify_cmd: &str, target_dir: &str) -> Envelope {
        write_plan_file(features);
        cmd("plan", vec![verify_cmd, target_dir])
    }

    fn plan_default() -> String {
        std::fs::create_dir_all("src/app").unwrap();
        std::fs::write("src/app/init.sh", "#!/usr/bin/env bash\nset -e\n").unwrap();
        plan(Some(&plan_cmd(FEATURES_JSON, "dotnet test", "src/app")))
    }

    /// Advances the flow until a feature is chosen and implemented (ready for verify),
    /// with no `verify-feature.sh` in the target (the deterministic fallback fails).
    fn advance_to_verify() {
        plan_default();
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

        advance_to_verify(); // session "dies" before verify

        let result = start();

        assert!(result.contains(r#""value":"implement"#));
        assert_eq!(feature_store::load().len(), 2);
        assert_eq!(feature_store::pending_count(), 2);
        assert_eq!(run_config_store::load().verify_cmd, "dotnet test");
        assert_eq!(run_config_store::load().target_dir, "src/app");
    }

    #[test]
    fn start_com_feature_pendente_preserva_o_run_id_do_plan_anterior() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify(); // session "dies" before verify
        let run_id_before_start = run_config_store::load().run_id;
        assert!(!run_id_before_start.is_empty());

        start();

        // Resuming doesn't generate a new run - run identity has to survive "start".
        assert_eq!(run_config_store::load().run_id, run_id_before_start);
    }

    // --- brief: persistence in start() and reinjection in implement ----------------------

    fn given_docs_brief(content: &str) {
        std::fs::create_dir_all("specs").unwrap();
        std::fs::write("specs/brief.md", content).unwrap();
    }

    #[test]
    fn start_com_docs_populados_persiste_o_brief_no_artifact_store() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        given_docs_brief("# Brief\n\nBuild a task-management app.");

        start();

        // docs_reader::read prepends a "## <file>" heading — contains, not equality.
        assert!(artifact_store::read("brief").contains("Build a task-management app."));
    }

    #[test]
    fn start_modo_interativo_nao_persiste_brief() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        start(); // no specs/ → initializer_interactive()

        assert_eq!(artifact_store::read("brief"), "");
    }

    #[test]
    fn start_novo_run_sem_docs_apaga_brief_do_run_anterior() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        // A second run with the SAME specs/ would already self-correct via overwrite (it
        // doesn't prove anything about reset()); the case only artifact_store::reset()
        // solves is specs→interactive: interactive mode never calls write, so without
        // reset() the old brief would leak through.
        given_docs_brief("brief do topico A");
        start();
        plan_default();
        for f in feature_store::load() {
            feature_store::mark_passed(f.id);
        }
        std::fs::remove_dir_all("specs").unwrap();

        start(); // run novo, sem specs/ → interativo

        assert_eq!(artifact_store::read("brief"), "");
    }

    #[test]
    fn plan_retorna_implement_sem_reinjetar_o_brief() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        given_docs_brief("brief do topico A");
        start();

        let result = plan_default();

        assert!(!result.contains("brief do topico A"));
    }

    #[test]
    fn pick_retorna_implement_sem_reinjetar_o_brief() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        given_docs_brief("brief do topico A");
        start();
        let result = plan_default();

        assert!(!result.contains("brief do topico A"));
    }

    #[test]
    fn bearings_e_implement_sem_brief_persistido_nao_tem_tag_brief() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        // No specs/: interactive mode, no persisted brief — the block disappears, not empty.
        let bearings_result = plan_default();
        let implement_result = bearings_result.clone();

        assert!(!bearings_result.contains("<brief>"));
        assert!(!implement_result.contains("<brief>"));
    }

    #[test]
    fn pick_retorna_implement_com_description_e_references_da_feature() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let json = r#"[{"id":1,"title":"A","priority":2,"description":"faz X","references":["RF-003"],"implementationContext":{"requirements":["inline X"]}},{"id":2,"title":"B","priority":1}]"#;
        std::fs::create_dir_all("src/app").unwrap();
        std::fs::write("src/app/init.sh", "#!/usr/bin/env bash\nset -e\n").unwrap();
        plan(Some(&plan_cmd(json, "dotnet test", "src/app"))); // escolhe "B"
        write_verify_feature_script(std::path::Path::new("src/app"), "#!/usr/bin/env bash\nset -e\n");
        let result = implement(Some(&cmd("implement", vec!["feito"]))); // verifica B, entrega A

        assert!(result.contains("Description: faz X"));
        assert!(result.contains("Brief references: RF-003"));
        assert!(result.contains("<implementation-context>requirements: inline X"));
        assert!(!result.contains("<brief>"));
    }

    #[test]
    fn pick_retorna_implement_sem_description_nem_references_nao_tem_bloco_de_contexto() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();
        let result = plan_default(); // FEATURES_JSON sem description/references

        assert!(!result.contains("Description:"));
        assert!(!result.contains("Brief references:"));
    }

    #[test]
    fn plan_persiste_features_e_roteia_para_bearings() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        std::fs::create_dir_all("web").unwrap();
        std::fs::write("web/init.sh", "#!/usr/bin/env bash\nset -e\n").unwrap();

        let result = plan(Some(&plan_cmd(FEATURES_JSON, "npm test", "web")));

        assert_eq!(feature_store::load().len(), 2);
        assert_eq!(run_config_store::load().verify_cmd, "npm test");
        assert_eq!(run_config_store::load().target_dir, "web");
        assert!(result.contains(r#""value":"implement"#));
    }

    #[test]
    fn plan_gera_um_run_id_novo_e_nao_vazio() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan(Some(&plan_cmd(FEATURES_JSON, "npm test", "web")));

        let run_id = run_config_store::load().run_id;

        assert!(!run_id.is_empty());
        assert!(uuid::Uuid::parse_str(&run_id).is_ok());
    }

    #[test]
    fn plan_features_invalidas_reemite_o_plano() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let result = plan(Some(&plan_cmd("not json", "dotnet test", ".")));

        assert!(feature_store::load().is_empty());
        assert_eq!(run_config_store::load(), RunConfig::default());
        assert!(result.contains(r#""value":"plan"#));
        assert!(!result.contains("NEW SESSION"));
    }

    #[test]
    fn plan_depends_on_ciclico_reemite_o_plano() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let json = r#"[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]"#;
        let result = plan(Some(&plan_cmd(json, "dotnet test", ".")));

        assert!(feature_store::load().is_empty());
        assert!(result.contains(r#""value":"plan"#));
        assert!(!result.contains("NEW SESSION"));
    }

    #[test]
    fn plan_corte_max_features_remove_dependencia_para_id_cortado() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        // id 1 (prioridade 1, a melhor) sobrevive ao corte; depende do id 2, cuja
        // priority (1000) is the worst of all — guaranteed to be cut by the cutoff at
        // MAX_FEATURES. Os "extras" preenchem as vagas restantes.
        let extras: String = (3..3 + MAX_FEATURES - 1)
            .map(|i| format!(r#"{{"id":{i},"title":"extra{i}","priority":{i}}}"#))
            .collect::<Vec<_>>()
            .join(",");
        let json = format!(
            r#"[{{"id":1,"title":"sobrevivente","priority":1,"dependsOn":[2]}},{{"id":2,"title":"cortada","priority":1000}},{extras}]"#
        );

        plan(Some(&plan_cmd(&json, "dotnet test", ".")));

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

        let implement_prompt = plan_default();

        assert_eq!(
            state_store::get(CURRENT_FEATURE_ID_KEY),
            Some("2".to_string())
        ); // prioridade 1 = id 2 ("B")
        assert!(implement_prompt.contains('B'));
        assert!(implement_prompt.contains(r#""value":"implement"#));
        assert!(implement_prompt.contains("=== NEW SESSION (clean context) ==="));
    }

    #[test]
    fn pick_respeita_dependencia_escolhe_dependencia_antes_da_dependente() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        let json = r#"[{"id":1,"title":"foundation","priority":2},{"id":2,"title":"depende","priority":1,"dependsOn":[1]}]"#;
        std::fs::create_dir_all("src/app").unwrap();
        std::fs::write("src/app/init.sh", "#!/usr/bin/env bash\nset -e\n").unwrap();
        plan(Some(&plan_cmd(json, "dotnet test", "src/app")));

        assert_eq!(
            state_store::get(CURRENT_FEATURE_ID_KEY),
            Some("1".to_string())
        );
    }

    #[test]
    fn pick_sem_feature_pronta_mas_com_pendencia_encerra_sem_reportar_concluido() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default(); // populates run_config; the list will be overwritten next
        feature_store::write(&[
            Feature {
                id: 1,
                title: "A".to_string(),
                priority: 1,
                passes: false,
                depends_on: vec![2],
                description: String::new(),
                references: Vec::new(),
                implementation_context: harness_engine::feature_store::ImplementationContext::default(),
            },
            Feature {
                id: 2,
                title: "B".to_string(),
                priority: 2,
                passes: false,
                depends_on: vec![1],
                description: String::new(),
                references: Vec::new(),
                implementation_context: harness_engine::feature_store::ImplementationContext::default(),
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

        assert!(result.contains("FAILED"));
        assert!(result.contains(r#""value":"implement"#));
        assert!(!result.contains("NEW SESSION"));
    }

    #[test]
    fn verify_pass_executa_handoff_automatico_e_avanca() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        write_verify_feature_script(std::path::Path::new("src/app"), "#!/usr/bin/env bash\nset -e\n");

        let result = verify(Some(&cmd("verify", vec!["PASS"])));

        assert!(result.contains(r#""value":"implement"#));
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

        assert!(result.contains(r#""value":"implement"#));
        assert!(!result.contains(r#""value":"handoff"#));
        assert!(result.contains("FAILED"));
    }

    #[test]
    fn implement_com_verify_feature_passando_executa_verify_e_handoff_automaticos() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        write_verify_feature_script(
            std::path::Path::new("src/app"),
            "#!/usr/bin/env bash\nset -euo pipefail\necho \"PASS: feature $1 verificada\"\n",
        );

        let result = implement(Some(&cmd("implement", vec!["implementei"])));

        assert!(result.contains(r#""value":"implement"#));
        assert!(!result.contains(r#""value":"verify"#));
        assert_eq!(feature_store::pending_count(), 1);
        let progress = std::fs::read_to_string("src/app/progress.txt").unwrap();
        assert!(progress.contains("Feature #2"));
        assert!(progress.contains("PASS: verify-feature.sh 2 passed"));
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

        let result = implement(Some(&cmd("implement", vec!["implementei"])));

        assert!(result.contains("FAILED"));
        assert!(result.contains("feature 2 quebrou"));
        assert!(result.contains(".harness/logs/verify-feature-2.log"));
        assert!(!result.contains("LINHA DETALHADA QUE FICA SO NO LOG"));
        assert!(result.contains(r#""value":"implement"#));
        assert_eq!(feature_store::pending_count(), 2);
        assert!(!std::path::Path::new("src/app/progress.txt").exists());
    }

    #[test]
    fn handoff_sem_pass_deterministico_retorna_para_verify() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = handoff_task(Some(&cmd("handoff", vec![""])));

        assert!(result.contains(r#""value":"verify"#));
        assert_eq!(feature_store::pending_count(), 2);
    }

    #[test]
    fn handoff_hash_textual_nao_substitui_verify_deterministico() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        advance_to_verify();

        let result = handoff_task(Some(&cmd("handoff", vec!["abc123"])));

        assert!(result.contains(r#""value":"verify"#));
        assert_eq!(feature_store::pending_count(), 2);
    }

    #[test]
    fn guarda_por_feature_ao_exceder_o_teto_encerra() {
        let _guard = lock_cwd();
        let _iso = Isolated::new();

        plan_default();
        bearings(Some(&cmd("bearings", vec!["ok"]))); // zera para 1
        state_store::set(FEATURE_STEPS_KEY, &STEPS_PER_FEATURE.to_string()); // no limite

        let result = smoke(Some(&cmd("smoke", vec!["ok"]))); // next bump goes over

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
