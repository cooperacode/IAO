//! Builds the development flow's prompts — the "strategy" kept separate from the state
//! machine in `tasks`. Each step references its output token via a constant (`$XXX`): the
//! same name the driver fills in and returns as the next envelope's arg.

use harness_engine::envelope::{Envelope, envelope_type};
use harness_engine::feature_store::{self, Feature};
use harness_engine::{
    context_policy, prompt_formatter, run_config_store, state_store,
};

use crate::tasks::{
    CURRENT_FEATURE_ID_KEY, CURRENT_FEATURE_TITLE_KEY,
};

// Output tokens (the driver stores the step's artifact in these and returns them as args).
const FEATURES: &str = "$FEATURES";
const VERIFY_CMD: &str = "$VERIFY_CMD";
const TARGET_DIR: &str = "$TARGET_DIR";

const FEATURES_SHAPE: &str = r#"[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[],"implementationContext":"..."}, ...]"#;

// Returns the current feature's bounded inline context for implement/fix prompts.
fn feature_context_block(feature: &Feature) -> String {
    if feature.description.trim().is_empty()
        && feature.references.is_empty()
        && feature.implementation_context.trim().is_empty()
    {
        return "\n".to_string();
    }

    let references = if feature.references.is_empty() {
        "none".to_string()
    } else {
        feature.references.join(", ")
    };
    let context = feature
        .implementation_context
        .replace("\r\n", "\\n")
        .replace('\n', "\\n");
    let implementation_context = if context.trim().is_empty() {
        String::new()
    } else {
        format!("<implementation-context>{context}</implementation-context>\n")
    };
    format!(
        "Description: {}\nBrief references: {references}\n{implementation_context}\n",
        feature.description
    )
}

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

fn current_feature_context_block() -> String {
    let Ok(feature_id) = state(CURRENT_FEATURE_ID_KEY).parse::<i32>() else {
        return String::new();
    };
    feature_store::load()
        .into_iter()
        .find(|feature| feature.id == feature_id)
        .map(|feature| feature_context_block(&feature))
        .unwrap_or_default()
}

// --- session 0: initializer -------------------------------------------------

pub fn initializer_prompt(content: &str, files: &[String]) -> String {
    let sources = files.join(", ");
    let input = format!(
        "Initialize the development run from this brief by following the injected\n\
`dev-initializer` skill:\n\
\n\
<brief sources=\"{sources}\">\n\
{content}\n\
</brief>\n\
\n\
Store a JSON ARRAY in '{FEATURES}': {FEATURES_SHAPE}\n\
(just the array, no passes — every feature is born pending). Store the verify\n\
command in '{VERIFY_CMD}' (e.g. `dotnet test`, `npm test`) and the target directory\n\
in '{TARGET_DIR}'. The `verify-feature.sh` may run the full suite at the start:\n\
`./init.sh`, then `$VERIFY_CMD`, print `PASS: feature <id> ...` and exit 0."
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
        "No brief was supplied. Ask the user for the goal, target directory, and\n\
established verification command, then follow the injected `dev-initializer` skill.\n\
\n\
Store a JSON ARRAY in '{FEATURES}' {FEATURES_SHAPE},\n\
the command in '{VERIFY_CMD}' and the directory in '{TARGET_DIR}'. The `verify-feature.sh`\n\
may run the full suite at the start: `./init.sh`, then `$VERIFY_CMD`, print\n\
`PASS: feature <id> ...` and exit 0."
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
        "Could not parse the feature list. Resend in '{FEATURES}' a valid JSON\n\
ARRAY, in exactly the format {FEATURES_SHAPE} — just the array, no surrounding text.\n\
Repeat the command `{VERIFY_CMD}` and `{TARGET_DIR}`."
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

pub fn smoke_fix_prompt(failure: &str) -> String {
    let input = format!(
        "The deterministic smoke test failed: {failure}\nRepair the target setup using `dev-smoke`, then return `smoke` without arguments. The harness will rerun `init.sh` and decide from its exit code."
    );
    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "smoke", vec![]),
        Some(&prompt_formatter::skills(&["dev-smoke"])),
    )
}

pub fn implement_prompt(feature: &Feature) -> String {
    let target_dir = run_config_store::load().target_dir;
    let context = feature_context_block(feature);
    let input = format!(
        "{}\
Follow `dev-implement` for this feature:\n\
Feature #{} (priority {}): {}\n\
{context}\
Target directory: {target_dir}\n\
\n\
Return `implement` without arguments when done. The harness derives the summary from Git.",
        context_policy::new_feature_prefix(),
        feature.id,
        feature.priority,
        feature.title
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "implement", vec![]),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn verify_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "The deterministic verifier could not be started for feature #{feature_id} ({}) in target directory ({}). Repair it using `dev-verify`, then return `verify` without arguments. The harness reruns the verifier and decides from its process result.",
        state(CURRENT_FEATURE_TITLE_KEY),
        config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "verify", vec![]),
        Some(&prompt_formatter::skills(&["dev-verify"])),
    )
}

pub fn verify_retry_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "The deterministic verifier is unavailable for feature #{feature_id} in target directory ({}). Repair it using `dev-verify`, then return `verify` without arguments for another harness-controlled attempt.",
        config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "verify", vec![]),
        Some(&prompt_formatter::skills(&["dev-verify"])),
    )
}

pub fn fix_prompt(verify_failure: Option<&str>) -> String {
    let failure = match verify_failure {
        Some(f) if !f.trim().is_empty() => format!("Failure observed: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "Verification FAILED on feature #{}\n\
({}).\n{}{}Follow `dev-implement` to fix only this feature.\n\
Return `implement` without arguments; the harness derives the new summary from Git.",
        state(CURRENT_FEATURE_ID_KEY),
        state(CURRENT_FEATURE_TITLE_KEY),
        current_feature_context_block(),
        failure
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "implement", vec![]),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn handoff_prompt(automatic_failure: Option<&str>) -> String {
    let failure = match automatic_failure {
        Some(f) if !f.trim().is_empty() => format!("Automatic handoff failed: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "{failure}Repair the repository/progress state using `dev-handoff`, then return\n\
`handoff` without arguments. The harness will inspect the repository and retry the real handoff."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}
