//! Automatic self-verify: runs `verify-feature.sh <id>` in the target directory, with a
//! time ceiling (derived from `harness_config.timeout_ms`) and a full log in
//! `.harness/logs/`.

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

pub fn try_automated_verify(feature_id: i32, target_dir: &Path, verify_cmd: &str) -> AutomatedVerifyResult {
    let script = target_dir.join("verify-feature.sh");
    let (command, label, is_script) = if script.is_file() {
        (vec!["bash".to_string(), script.to_string_lossy().to_string(), feature_id.to_string()], format!("bash ./verify-feature.sh {feature_id}"), true)
    } else {
        let args = configured_verify_argv(verify_cmd);
        if args.is_empty() { return AutomatedVerifyResult::missing(); }
        (args.clone(), args.join(" "), false)
    };

    let result = run_verify_script(target_dir, &command);
    let log_path = write_verify_log(target_dir, &label, feature_id, &result);

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

fn write_verify_log(
    target_dir: &Path,
    command: &str,
    feature_id: i32,
    result: &VerifyScriptResult,
) -> String {
    let relative_dir = ".harness/logs";
    let relative_path: PathBuf = [relative_dir, &format!("verify-feature-{feature_id}.log")]
        .iter()
        .collect();
    let display_path = relative_path.to_string_lossy().replace('\\', "/");

    let full_path = target_dir.join(&relative_path);
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
