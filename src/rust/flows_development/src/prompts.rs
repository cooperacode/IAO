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
const NOTE: &str = "$NOTE";
const SMOKE: &str = "$SMOKE";
const SUMMARY: &str = "$SUMMARY";
const RESULT: &str = "$RESULT";
const COMMIT: &str = "$COMMIT";

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

// --- per-feature loop (one fresh-context session) --------------------------

pub fn bearings_prompt() -> String {
    let brief = brief_block();
    let input = format!(
        "=== NEW SESSION (clean context) ===\n\
You are a coding agent starting a FRESH session. Do not assume anything from the\n\
previous session — all state lives in the persistent artifacts.\n\
{brief}\
Get your bearings with short output: run `pwd`, read only the tail of `progress.txt` and the\n\
recent `git log --oneline` to understand what has already been done. Do not paste long\n\
logs; if you need to preserve detail, save it in `.harness/logs/`.\n\
\n\
Summarize what you found in '{NOTE}' in 2-4 lines."
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
        "Smoke test: run `./init.sh` in the target directory ({target_dir}) and confirm\n\
that the baseline comes up/builds without error before touching any feature. Save the\n\
full output to `.harness/logs/smoke.log` and report in '{SMOKE}' just `ok` or the\n\
main error and the log path."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "smoke", vec![SMOKE.to_string()]),
        Some(&prompt_formatter::skills(&["dev-smoke"])),
    )
}

pub fn pick_prompt() -> String {
    let input = "Baseline confirmed. Send the `pick` command to receive the next feature to\n\
implement (the highest-priority one still pending — the harness chooses).";

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
        "Implement EXCLUSIVELY this feature, incrementally and minimally — nothing beyond\n\
it:\n\
{brief}\
Feature #{} (priority {}): {}\n\
{context}\
Work in the target directory ({target_dir}). If you run commands with\n\
long output, save it to `.harness/logs/` and do not paste logs into the summary. When done,\n\
summarize what you implemented in '{SUMMARY}' in one short sentence.",
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
        "The harness did not find `verify-feature.sh` in the target directory, so do a\n\
manual self-verify of feature #{feature_id}\n\
({}) the way a user would: run\n\
`{}` in the target directory ({}) and\n\
confirm the behavior end to end. Save the full output to\n\
`.harness/logs/verify-{feature_id}.log`.\n\
\n\
Respond in '{RESULT}' starting with `PASS` or `FAIL: <reason>`, including only the\n\
main error and the log path.",
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
        "The self-verify verdict did not start with `PASS` or `FAIL`. Re-run, if\n\
needed, `{}` in the target directory ({})\n\
saving the full output to `.harness/logs/verify-{feature_id}.log`.\n\
Respond in '{RESULT}' starting exactly with `PASS` or `FAIL: <reason>`,\n\
without pasting long logs.",
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
        Some(f) if !f.trim().is_empty() => format!("Failure observed: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "Verification FAILED on feature #{}\n\
({}). {failure}Fix the implementation (still ONLY this feature).\n\
If you check logs, read only the relevant excerpt. Summarize the fix in '{SUMMARY}' —\n\
we'll verify again next.",
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
        Some(f) if !f.trim().is_empty() => format!("Automatic handoff failed: {f}\n\n"),
        _ => String::new(),
    };

    let input = format!(
        "{failure}Leave the state CLEAN for the next session:\n\
1. `git commit` with a descriptive message referencing feature #{}. If the target directory is not a Git repository, record this explicitly as `NO_GIT: <reason>`.\n\
2. Append a line to `progress.txt` in this exact format (same as the automatic handoff, so entries stay consistent): `[YYYY-MM-DD HH:MM UTC] Feature #<id> - <title>: <what was done>. Verify with: <command>. Result: <result>`.\n\
\n\
Confirm with the commit hash or `NO_GIT: <reason>` in '{COMMIT}'.",
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
        "The handoff confirmation came back empty. Update `progress.txt` in the target directory\n\
({target_dir}) and respond in '{COMMIT}' with the commit hash or\n\
`NO_GIT: <reason>` when there is no Git repository."
    );

    prompt_formatter::format(
        &input,
        &Envelope::new(envelope_type::COMMAND, "handoff", vec![COMMIT.to_string()]),
        Some(&prompt_formatter::skills(&["dev-handoff"])),
    )
}
