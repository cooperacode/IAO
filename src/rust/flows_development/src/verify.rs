//! Automatic verification: runs `verify-feature.sh <id>` in the target directory, with a
//! time ceiling (derived from `harness_config.timeout_ms`) and a full log in
//! `.harness/logs/`. When `RunConfig.verify_cmds` holds a non-empty list instead, every
//! command in it runs CONCURRENTLY (one log each), with an AND verdict — see
//! `try_parallel_configured_verify`.

use std::io::Read;
use std::path::{Path, PathBuf};
use std::process::{Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use harness_engine::harness_config;

pub struct AutomatedVerifyResult {
    pub attempted: bool,
    pub success: bool,
    pub result: String,
}

impl AutomatedVerifyResult {
    fn missing() -> Self {
        Self {
            attempted: false,
            success: false,
            result: String::new(),
        }
    }

    fn passed(result: String) -> Self {
        Self {
            attempted: true,
            success: true,
            result,
        }
    }

    fn failed(result: String) -> Self {
        Self {
            attempted: true,
            success: false,
            result,
        }
    }
}

struct VerifyScriptResult {
    exit_code: i32,
    output: String,
    error: String,
    timed_out: bool,
}

pub fn try_automated_verify(feature_id: i32, target_dir: &Path, verify_cmd: &str, verify_cmds: &[String]) -> AutomatedVerifyResult {
    let script = target_dir.join("verify-feature.sh");
    let (command, label, is_script) = if script.is_file() {
        (vec!["bash".to_string(), script.to_string_lossy().to_string(), feature_id.to_string()], format!("bash ./verify-feature.sh {feature_id}"), true)
    } else if !verify_cmds.is_empty() {
        // Configured as a list (RunConfig.verify_cmds): run every command concurrently,
        // AND verdict, instead of the single-command path below. Takes precedence over
        // `verify_cmd` but not over `verify-feature.sh`, same ordering as the single
        // command had relative to the script.
        return try_parallel_configured_verify(target_dir, verify_cmds, feature_id);
    } else {
        let args = configured_verify_argv(verify_cmd);
        if args.is_empty() { return AutomatedVerifyResult::missing(); }
        (args.clone(), args.join(" "), false)
    };

    let result = run_verify_script(target_dir, &command);
    let log_path = write_verify_log(target_dir, &label, feature_id, &result, None);

    if result.timed_out {
        return AutomatedVerifyResult::failed(format!(
            "FAIL: verification exceeded timeout ({}){}",
            verify_timeout_description(),
            verify_output_suffix(&result, &log_path)
        ));
    }

    if result.exit_code == 0 {
        return AutomatedVerifyResult::passed(format!("PASS: {} passed{}", if is_script { format!("verify-feature.sh {feature_id}") } else { "configured verify command".to_string() }, log_suffix(&log_path)));
    }

    AutomatedVerifyResult::failed(format!(
        "FAIL: verification failed (exit {}){}",
        result.exit_code,
        verify_output_suffix(&result, &log_path)
    ))
}

/// Runs every entry in `commands` CONCURRENTLY (one `std::thread::spawn` each — no
/// tokio/rayon in this workspace, mirroring the reader-thread pattern in
/// `run_verify_script` and the abandon-on-timeout thread in
/// `task_registry::run_with_timeout`), and ANDs the verdicts: passes only if every command
/// exits 0. Each command gets its own log (`verify-feature-{id}-{n}.log`, 1-based `n`), and
/// the aggregate message lists every command's outcome, not just the first failure.
fn try_parallel_configured_verify(target_dir: &Path, commands: &[String], feature_id: i32) -> AutomatedVerifyResult {
    // Tokenize everything up front, before launching any subprocess: a single disallowed
    // command fails the whole check immediately, without side-launching the others.
    let mut argvs: Vec<Vec<String>> = Vec::with_capacity(commands.len());
    for (i, raw) in commands.iter().enumerate() {
        let args = configured_verify_argv(raw);
        if args.is_empty() {
            return AutomatedVerifyResult::failed(format!(
                "FAIL: verify command #{} is empty or uses disallowed shell operators: {raw}",
                i + 1
            ));
        }
        argvs.push(args);
    }

    let target_dir_owned = target_dir.to_path_buf();
    let handles: Vec<_> = argvs
        .into_iter()
        .map(|args| {
            let target_dir = target_dir_owned.clone();
            thread::spawn(move || {
                let label = args.join(" ");
                let result = run_verify_script(&target_dir, &args);
                (label, result)
            })
        })
        .collect();

    let total = handles.len();
    let mut failures = 0usize;
    let mut lines: Vec<String> = Vec::with_capacity(total);
    for (i, handle) in handles.into_iter().enumerate() {
        let n = i + 1;
        match handle.join() {
            Ok((label, result)) => {
                let log_path = write_verify_log(target_dir, &label, feature_id, &result, Some(n));
                if result.timed_out {
                    failures += 1;
                    lines.push(format!(
                        "#{n} TIMEOUT ({}): {label}{}",
                        verify_timeout_description(),
                        log_suffix(&log_path)
                    ));
                } else if result.exit_code == 0 {
                    lines.push(format!("#{n} PASS: {label}{}", log_suffix(&log_path)));
                } else {
                    failures += 1;
                    lines.push(format!(
                        "#{n} FAIL (exit {}): {label}{}",
                        result.exit_code,
                        verify_output_suffix(&result, &log_path)
                    ));
                }
            }
            // A worker thread panicking (a bug, not a check failure) must not take down the
            // whole verify step — treated as a failed check for this one command, the
            // others' results are unaffected since each thread is independent.
            Err(_) => {
                failures += 1;
                lines.push(format!(
                    "#{n} FAIL: verify command panicked before completing: {}",
                    commands[i]
                ));
            }
        }
    }

    let joined = lines.join(" | ");
    if failures == 0 {
        AutomatedVerifyResult::passed(format!("PASS: all {total} verify commands passed. {joined}"))
    } else {
        AutomatedVerifyResult::failed(format!(
            "FAIL: {failures} of {total} verify commands did not pass. {joined}"
        ))
    }
}

fn run_verify_script(target_dir: &Path, command: &[String]) -> VerifyScriptResult {
    let child = Command::new(&command[0])
        .args(&command[1..])
        .current_dir(target_dir)
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn();

    let mut child = match child {
        Ok(c) => c,
        Err(e) => {
            return VerifyScriptResult {
                exit_code: -1,
                output: String::new(),
                error: e.to_string(),
                timed_out: false,
            };
        }
    };

    // Reader threads drain the pipes continuously — without this, a script with large
    // output would hang writing into the full pipe while the poll loop below only
    // observes the status, without reading anything (the same problem that .NET's
    // `ReadToEndAsync` and Python's `subprocess.run(timeout=...)` internal drain avoid).
    let mut stdout_pipe = child.stdout.take().expect("stdout piped");
    let mut stderr_pipe = child.stderr.take().expect("stderr piped");
    let stdout_handle = thread::spawn(move || {
        let mut buf = String::new();
        let _ = stdout_pipe.read_to_string(&mut buf);
        buf
    });
    let stderr_handle = thread::spawn(move || {
        let mut buf = String::new();
        let _ = stderr_pipe.read_to_string(&mut buf);
        buf
    });

    let timeout_ms = verify_timeout_ms();
    let deadline = if timeout_ms > 0 {
        Some(Instant::now() + Duration::from_millis(timeout_ms as u64))
    } else {
        None
    };

    let (exit_code, timed_out) = loop {
        match child.try_wait() {
            Ok(Some(status)) => break (status.code().unwrap_or(-1), false),
            Ok(None) => {
                if let Some(deadline) = deadline {
                    if Instant::now() >= deadline {
                        let _ = child.kill();
                        let _ = child.wait();
                        break (-1, true);
                    }
                }
                thread::sleep(Duration::from_millis(20));
            }
            Err(_) => break (-1, false),
        }
    };

    VerifyScriptResult {
        exit_code,
        output: stdout_handle.join().unwrap_or_default(),
        error: stderr_handle.join().unwrap_or_default(),
        timed_out,
    }
}

// Verify's time ceiling: a margin under the global timeout, so the harness still has a
// chance to report the overrun before dispatch's own step guard cuts it off.
fn verify_timeout_ms() -> i32 {
    let timeout_ms = harness_config::current().timeout_ms;
    if timeout_ms <= 0 {
        return 0;
    }
    let margin = (timeout_ms / 10).clamp(1, 500);
    (timeout_ms - margin).max(1)
}

fn verify_timeout_description() -> String {
    let timeout_ms = verify_timeout_ms();
    if timeout_ms <= 0 {
        "no limit".to_string()
    } else {
        format!("{timeout_ms}ms")
    }
}

// `check_index` distinguishes one of several concurrent verify commands (1-based, from
// `try_parallel_configured_verify`) from the single-command path: `Some(n)` names the log
// `verify-feature-{id}-{n}.log`, `None` keeps the original `verify-feature-{id}.log`.
fn write_verify_log(
    target_dir: &Path,
    command: &str,
    feature_id: i32,
    result: &VerifyScriptResult,
    check_index: Option<usize>,
) -> String {
    let relative_dir = ".harness/logs";
    let file_name = match check_index {
        Some(n) => format!("verify-feature-{feature_id}-{n}.log"),
        None => format!("verify-feature-{feature_id}.log"),
    };
    let relative_path: PathBuf = [relative_dir, &file_name].iter().collect();
    let display_path = relative_path.to_string_lossy().replace('\\', "/");

    let full_path = relative_path.clone();
    let write_result = full_path
        .parent()
        .map(std::fs::create_dir_all)
        .unwrap_or(Ok(()))
        .and_then(|_| {
            std::fs::write(
                &full_path,
                format!(
                    "timestampUtc: {}\n\
                    command: {command}\n\
cwd: {}\n\
exitCode: {}\n\
timedOut: {}\n\
\n\
--- stdout ---\n\
{}\n\
\n\
--- stderr ---\n\
{}",
                    chrono::Utc::now().to_rfc3339_opts(chrono::SecondsFormat::Secs, true),
                    target_dir.display(),
                    result.exit_code,
                    result.timed_out,
                    result.output,
                    result.error
                ),
            )
        });

    match write_result {
        Ok(()) => display_path,
        Err(e) => format!(
            "log unavailable ({})",
            crate::handoff::one_line(&e.to_string(), "")
        ),
    }
}

fn configured_verify_argv(raw: &str) -> Vec<String> {
    let text = raw.trim();
    if text.is_empty() || [";", "&", "|", "<", ">", "`", "$"].iter().any(|x| text.contains(x)) { return Vec::new(); }
    let args = text.split_whitespace().map(str::to_string).collect::<Vec<_>>();
    if args.is_empty() { return args; }
    let bin = std::path::Path::new(&args[0]).file_name().and_then(|x| x.to_str()).unwrap_or("").to_ascii_lowercase();
    if ["sh", "bash", "zsh", "fish", "cmd", "powershell", "pwsh"].contains(&bin.as_str()) && args.iter().skip(1).any(|x| ["-c", "-command", "/c"].contains(&x.as_str())) { return Vec::new(); }
    args
}

fn verify_output_suffix(result: &VerifyScriptResult, log_path: &str) -> String {
    let output = snippet(&first_meaningful_line(&[&result.output, &result.error]));
    if output.trim().is_empty() {
        log_suffix(log_path)
    } else {
        format!(": {output}{}", log_suffix(log_path))
    }
}

fn first_meaningful_line(values: &[&str]) -> String {
    for value in values {
        for line in value.replace('\r', "\n").split('\n') {
            let trimmed = line.trim();
            if !trimmed.is_empty() {
                return trimmed.to_string();
            }
        }
    }
    String::new()
}

fn log_suffix(log_path: &str) -> String {
    if log_path.trim().is_empty() {
        String::new()
    } else {
        format!(". Log: {log_path}")
    }
}

fn snippet(value: &str) -> String {
    let text = crate::handoff::one_line(value, "");
    const MAX_BYTES: usize = 240;
    if text.len() <= MAX_BYTES {
        text
    } else {
        let truncated = crate::handoff::truncate_utf8_bytes(&text, MAX_BYTES);
        truncated.trim_end().to_string() + "..."
    }
}
