//! Automatic handoff: git commit of the target directory (excluding `.harness/`) + a line
//! in `progress.txt`, without spending a model turn. A failure at any step falls back to
//! the legacy manual-repair prompt (`prompts::handoff_prompt`).

use std::path::{Path, PathBuf};

use harness_engine::{feature_store, git_command, run_config_store, state_store};

use crate::prompts;
use crate::tasks::{CURRENT_FEATURE_ID_KEY, CURRENT_FEATURE_SUMMARY_KEY, CURRENT_FEATURE_TITLE_KEY};

fn state(key: &str) -> String {
    state_store::get(key).unwrap_or_default()
}

pub fn complete_verified_feature(verify_result: &str) -> String {
    match try_automated_handoff(verify_result) {
        Ok(confirmation) => {
            eprintln!("[dev] automatic handoff completed: {confirmation}");
            if let Ok(id) = state(CURRENT_FEATURE_ID_KEY).parse::<i32>() {
                feature_store::mark_passed(id);
            }
            if feature_store::all_passing() {
                crate::tasks::done()
            } else {
                prompts::bearings_prompt()
            }
        }
        Err(failure) => {
            eprintln!("[dev] automatic handoff failed: {failure}");
            prompts::handoff_prompt(Some(&failure))
        }
    }
}

fn try_automated_handoff(verify_result: &str) -> Result<String, String> {
    let feature_id: i32 = state(CURRENT_FEATURE_ID_KEY)
        .parse()
        .map_err(|_| "current feature missing from state.json".to_string())?;

    let feature = feature_store::load()
        .into_iter()
        .find(|f| f.id == feature_id);
    let mut title = feature
        .map(|f| f.title)
        .unwrap_or_else(|| state(CURRENT_FEATURE_TITLE_KEY));
    if title.trim().is_empty() {
        title = format!("feature #{feature_id}");
    }

    let config = run_config_store::load();
    let target_dir = resolve_target_dir(&config.target_dir)?;

    std::fs::create_dir_all(&target_dir)
        .map_err(|e| format!("failed to update progress.txt: {e}"))?;
    append_progress(
        &target_dir,
        feature_id,
        &title,
        &config.verify_cmd,
        verify_result,
    )
    .map_err(|e| format!("failed to update progress.txt: {e}"))?;

    let rev_parse = git_command::run(&target_dir, &["rev-parse", "--show-toplevel"]);
    if rev_parse.exit_code != 0 {
        return Ok(format!(
            "NO_GIT: {}",
            one_line(
                &rev_parse.error,
                "target directory is outside a Git repository"
            )
        ));
    }

    let add = git_command::run(&target_dir, &["add", "-A", "--", ".", ":(exclude).harness"]);
    if add.exit_code != 0 {
        return Err(format!(
            "git add failed: {}",
            one_line(&add.error, &add.output)
        ));
    }

    let diff = git_command::run(
        &target_dir,
        &[
            "diff",
            "--cached",
            "--quiet",
            "--",
            ".",
            ":(exclude).harness",
        ],
    );
    if diff.exit_code == 0 {
        let head = git_command::run(&target_dir, &["rev-parse", "--short", "HEAD"]);
        return Ok(if head.exit_code == 0 {
            one_line(&head.output, "NO_CHANGES")
        } else {
            "NO_CHANGES".to_string()
        });
    }
    if diff.exit_code > 1 {
        return Err(format!(
            "git diff --cached failed: {}",
            one_line(&diff.error, &diff.output)
        ));
    }

    let commit = git_command::run(
        &target_dir,
        &[
            "commit",
            "-m",
            &commit_message(feature_id, &title),
            "--",
            ".",
            ":(exclude).harness",
        ],
    );
    if commit.exit_code != 0 {
        return Err(format!(
            "git commit failed: {}",
            one_line(&commit.error, &commit.output)
        ));
    }

    let status = git_command::run(
        &target_dir,
        &["status", "--short", "--", ".", ":(exclude).harness"],
    );
    if status.exit_code != 0 {
        return Err(format!(
            "git status failed: {}",
            one_line(&status.error, &status.output)
        ));
    }
    if !status.output.trim().is_empty() {
        return Err(format!(
            "target directory still dirty after commit: {}",
            one_line(&status.output, "")
        ));
    }

    let hash = git_command::run(&target_dir, &["rev-parse", "--short", "HEAD"]);
    if hash.exit_code == 0 {
        Ok(one_line(&hash.output, "COMMIT_CREATED"))
    } else {
        Err(format!(
            "commit created, but the hash could not be read: {}",
            one_line(&hash.error, &hash.output)
        ))
    }
}

/// Resolves the handoff's target directory and rejects clearly dangerous or nonsensical
/// configurations before running `git add`/`git commit` in it. Full containment against a
/// signed policy root (capability broker) is future-phase work — this is just the RFC
/// §6.3 minimal rejection list: empty, filesystem root, the user's HOME, or the harness's
/// own install directory.
pub fn resolve_target_dir(target_dir: &str) -> Result<PathBuf, String> {
    let configured = target_dir.trim();
    if configured.is_empty() {
        return Err("target_dir empty: no target directory configured".to_string());
    }

    let cwd = std::env::current_dir().unwrap_or_default();
    let joined = cwd.join(configured);
    let resolved = joined.canonicalize().unwrap_or(joined);

    if resolved.parent().is_none() {
        return Err(format!(
            "target_dir resolves to the filesystem root: {}",
            resolved.display()
        ));
    }

    if let Ok(home) = std::env::var("HOME").or_else(|_| std::env::var("USERPROFILE")) {
        if !home.trim().is_empty() {
            let home_path = PathBuf::from(&home);
            let home_canonical = home_path.canonicalize().unwrap_or(home_path);
            if resolved == home_canonical {
                return Err(format!(
                    "target_dir resolves to the user's home directory: {}",
                    resolved.display()
                ));
            }
        }
    }

    if let Ok(exe) = std::env::current_exe() {
        if let Some(exe_dir) = exe.parent() {
            let exe_dir_canonical = exe_dir
                .canonicalize()
                .unwrap_or_else(|_| exe_dir.to_path_buf());
            if resolved == exe_dir_canonical {
                return Err(format!(
                    "target_dir resolves to the harness install directory: {}",
                    resolved.display()
                ));
            }
        }
    }

    Ok(resolved)
}

fn append_progress(
    target_dir: &Path,
    feature_id: i32,
    title: &str,
    verify_cmd: &str,
    verify_result: &str,
) -> std::io::Result<()> {
    let summary = one_line(&state(CURRENT_FEATURE_SUMMARY_KEY), "implementation completed");
    let verify = one_line(verify_result, "PASS");
    let command = if verify_cmd.trim().is_empty() {
        "the project's verify command".to_string()
    } else {
        verify_cmd.to_string()
    };
    let line = format!(
        "[{} UTC] Feature #{feature_id} - {}: {summary}. Verify with: {}. Result: {verify}",
        chrono::Utc::now().format("%Y-%m-%d %H:%M"),
        one_line(title, ""),
        one_line(&command, "")
    );

    if let Ok(existing) = std::fs::read_to_string(target_dir.join("progress.txt")) {
        let prefix = format!("Feature #{feature_id} - {}:", one_line(title, ""));
        if existing.lines().any(|l| l.contains(&prefix) && l.contains("Result:")) {
            return Ok(());
        }
    }

    use std::io::Write;
    let mut file = std::fs::OpenOptions::new()
        .create(true)
        .append(true)
        .open(target_dir.join("progress.txt"))?;
    writeln!(file, "{line}")
}

fn commit_message(feature_id: i32, title: &str) -> String {
    let mut suffix = one_line(title, "");
    if suffix.len() > 72 {
        suffix = truncate_utf8_bytes(&suffix, 72).trim_end().to_string();
    }
    format!("feat(development): complete feature #{feature_id} - {suffix}")
}

/// Cuts `text` at no more than `max_bytes` UTF-8 octets, backing off to the nearest valid
/// char boundary — never splits a multi-byte character (accent, emoji) in half. Shared by
/// `commit_message` and `verify::snippet` (RFC Appendix B item 1: measure in bytes, not
/// codepoints, so the semantics match across the .NET/Python/Rust engines).
pub fn truncate_utf8_bytes(text: &str, max_bytes: usize) -> String {
    if text.len() <= max_bytes {
        return text.to_string();
    }

    let mut cut = max_bytes;
    while cut > 0 && !text.is_char_boundary(cut) {
        cut -= 1;
    }

    text[..cut].to_string()
}

pub fn one_line(value: &str, fallback: &str) -> String {
    let normalized = value
        .replace('\r', " ")
        .replace('\n', " ")
        .split_whitespace()
        .collect::<Vec<_>>()
        .join(" ");
    if normalized.trim().is_empty() {
        fallback.to_string()
    } else {
        normalized.trim().to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truncate_utf8_bytes_does_not_split_a_multibyte_char_in_half() {
        // "café ☕" — "é" (2 bytes) and "☕" (3 bytes) are multibyte; a naive cut at any
        // position would produce an invalid UTF-8 string (panic when slicing).
        let text = "café ☕";
        for max in 0..=text.len() {
            let truncated = truncate_utf8_bytes(text, max);
            assert!(truncated.len() <= max);
        }
    }

    #[test]
    fn commit_message_truncates_a_long_title_on_utf8_byte_boundaries() {
        let title = "café ".repeat(20); // well above 72 bytes, with an accent near the cut
        let message = commit_message(1, &title);

        assert!(message.starts_with("feat(development): complete feature #1 - "));
        // If the cut had split an "é" in half, the formatting above would already have
        // panicked while building the String — reaching this point already proves the cut
        // was valid.
    }
}
