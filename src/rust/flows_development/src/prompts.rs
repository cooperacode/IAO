//! Builds the development flow's prompts — the "strategy" kept separate from the state
//! machine in `tasks`. Each step references its output token via a constant (`$XXX`): the
//! same name the driver fills in and returns as the next envelope's arg.

use harness_engine::envelope::{Envelope, envelope_type};
use harness_engine::feature_store::{DESCRIPTION_MAX_CHARS, Feature};
use harness_engine::{artifact_store, prompt_formatter, run_config_store, state_store};

use crate::tasks::{BRIEF_ARTIFACT_NAME, CURRENT_FEATURE_ID_KEY, CURRENT_FEATURE_TITLE_KEY};

// Output tokens (the driver stores the step's artifact in these and returns them as args).
const FEATURES: &str = "$FEATURES";
const VERIFY_CMD: &str = "$VERIFY_CMD";
const TARGET_DIR: &str = "$TARGET_DIR";

const FEATURES_SHAPE: &str =
    r#"[{"id":1,"title":"...","priority":1,"dependsOn":[],"description":"...","references":[]}, ...]"#;

// Reinjects description/references (feature_store::Feature) into the implement prompt —
// the only point of the loop that receives the whole Feature object, not just title/id via
// state_store. "" when the feature has neither (e.g. a feature_list.json from a version
// before these fields existed) — the block disappears, it doesn't show up empty.
fn feature_context_block(feature: &Feature) -> String {
    if feature.description.trim().is_empty() && feature.references.is_empty() {
        return "\n".to_string();
    }

    let references = if feature.references.is_empty() {
        "none".to_string()
    } else {
        feature.references.join(", ")
    };
    format!(
        "Description: {}\nBrief references: {references}\n\n",
        feature.description
    )
}

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

// Reinjects the persisted brief (artifact_store, BRIEF_ARTIFACT_NAME) at the two points of
// the loop that actually reason about "what to build" — bearings and implement — and only
// there: smoke/pick/verify/fix/handoff just run a script or do bookkeeping, with no need
// for scope context. Returns a blank line (the same paragraph break as before this
// feature) when there's no persisted brief — interactive mode, or resuming a run from
// before this feature — or the "<brief>" block when there is one. Same treatment as the
// skills (prompt_formatter::read_skills): line breaks become the literal "\n" marker and
// the whole block ends up on a single line — the brief content doesn't need to preserve
// its original Markdown formatting here, just be available. Always reinjecting the SAME
// text, byte for byte, is also the lowest-cost bet to benefit from the driver's underlying
// provider's prompt cache (not guaranteed: the harness only controls the emitted text, not
// whether the driver marks a cache breakpoint there).
fn brief_block() -> String {
    let brief = artifact_store::read(BRIEF_ARTIFACT_NAME);
    if brief.trim().is_empty() {
        return "\n".to_string();
    }

    let single_line = brief.replace("\r\n", "\\n").replace('\n', "\\n");
    format!("<brief>{single_line}</brief>\n")
}

// --- session 0: initializer -------------------------------------------------

pub fn initializer_prompt(content: &str, files: &[String]) -> String {
    let sources = files.join(", ");
    let input = format!(
        "You are the INITIALIZER (session 0). From the brief below:\n\
1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).\n\
2. Scaffold the target project's environment: create an idempotent `init.sh` that installs dependencies and brings up/builds the app, an idempotent `verify-feature.sh <id>` that verifies a feature, and the minimal folder structure.\n\
3. Expand the brief into a PRIORITIZED list of small, verifiable features, each independently implementable and testable. Number the priority (1 = highest). If a feature only makes sense after another one (e.g. it needs a schema another feature creates), record their ids in `dependsOn` — empty array when there is no dependency. The harness honors this order in addition to priority. Also fill in, for each feature: `description`, an objective description of what it does (up to {DESCRIPTION_MAX_CHARS} characters); and `references`, the explicit codes cited in the brief that relate to it (e.g. \"RF-003\", \"JIRA-142\", a named section) — empty array if the brief cites no explicit code for that feature (do not invent one).\n\
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
        "You are the INITIALIZER (session 0). Use the #tool:askQuestions and ask the user:\n\
(a) what to build (the app's goal), (b) the target directory, and (c) the verify\n\
command (e.g. `dotnet test`, `npm test`). Then:\n\
1. Ensure there is a Git repository in the target directory (run `git init` if needed) and create/reuse a dedicated working branch (never commit straight to main/master).\n\
2. Scaffold the environment: create an idempotent `init.sh` and an idempotent `verify-feature.sh <id>` in the target directory.\n\
3. Expand the goal into a PRIORITIZED list of small, verifiable features. If one depends on another, record their ids in `dependsOn` (empty array when there is none). Also fill in `description` (up to {DESCRIPTION_MAX_CHARS} characters) and `references` (explicit codes cited by the user for that feature; empty array if there are none).\n\
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
    let input = format!("Smoke failed deterministically: {failure}\nFix the target setup, then return `smoke` without arguments.");
    prompt_formatter::format(&input, &Envelope::new(envelope_type::COMMAND, "smoke", vec![]), Some(&prompt_formatter::skills(&["dev-smoke"])))
}

pub fn implement_prompt(feature: &Feature) -> String {
    let target_dir = run_config_store::load().target_dir;
    let brief = brief_block();
    let context = feature_context_block(feature);
    let input = format!(
        "=== NEW SESSION (clean context) ===\n\n\
Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond\n\
it:\n\
{brief}\
Feature #{} (priority {}): {}\n\
{context}\
Work in the target directory ({target_dir}). If you run commands with\n\
long output, save it to `.harness/logs/`. Return `implement` without arguments when done;\n\
the harness derives the summary from the actual Git diff.",
        feature.id, feature.priority, feature.title
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "implement",
            vec![],
        ),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn verify_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "The deterministic verifier could not be started for feature #{feature_id} ({}) in target directory ({}). Repair the verification setup and return `implement` without arguments.",
        state(CURRENT_FEATURE_TITLE_KEY),
        config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "implement", vec![]),
        Some(&prompt_formatter::skills(&["dev-verify"])),
    )
}

pub fn verify_retry_prompt() -> String {
    let config = run_config_store::load();
    let feature_id = state(CURRENT_FEATURE_ID_KEY);
    let input = format!(
        "The deterministic verifier is unavailable for feature #{feature_id} in target directory ({}). Repair or create it and return `implement` without arguments.",
        config.target_dir
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "implement", vec![]),
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
({}). {failure}Fix the implementation (still ONLY this feature).\n\
If you check logs, read only the relevant excerpt. Return `implement` without arguments;\n\
the harness derives the new summary from Git.",
        state(CURRENT_FEATURE_ID_KEY),
        state(CURRENT_FEATURE_TITLE_KEY)
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(
            envelope_type::COMMAND,
            "implement",
            vec![],
        ),
        Some(&prompt_formatter::skills(&["dev-implement"])),
    )
}

pub fn handoff_prompt(automatic_failure: Option<&str>) -> String {
    let failure = match automatic_failure {
        Some(f) if !f.trim().is_empty() => format!("Automatic handoff failed: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "{failure}Automatic handoff requires a deterministic PASS. Return `handoff` without arguments\n\
so the harness can retry the real progress/git operation."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}

pub fn handoff_retry_prompt() -> String {
    let input = "The handoff is deterministic and only runs after a recorded PASS. Return `handoff` without arguments after verification passes.";

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}
