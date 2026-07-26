//! Self-verify automático: roda `verify-feature.sh <id>` no diretório-alvo, com teto de
//! tempo (derivado de `harness_config.timeout_ms`) e log completo em `.harness/logs/`.

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

pub fn try_automated_verify(feature_id: i32, target_dir: &Path) -> AutomatedVerifyResult {
    let script = target_dir.join("verify-feature.sh");
    if !script.exists() {
        return AutomatedVerifyResult::missing();
    }

    let result = run_verify_script(target_dir, &script, feature_id);
    let log_path = write_verify_log(target_dir, &script, feature_id, &result);

    if result.timed_out {
        return AutomatedVerifyResult::failed(format!(
            "FAIL: verify-feature.sh {feature_id} excedeu timeout ({}){}",
            verify_timeout_description(),
            verify_output_suffix(&result, &log_path)
        ));
    }

    if result.exit_code == 0 {
        return AutomatedVerifyResult::passed(pass_result(
            feature_id,
            &result.output,
            &result.error,
            &log_path,
        ));
    }

    AutomatedVerifyResult::failed(format!(
        "FAIL: verify-feature.sh {feature_id} falhou (exit {}){}",
        result.exit_code,
        verify_output_suffix(&result, &log_path)
    ))
}

fn run_verify_script(target_dir: &Path, script: &Path, feature_id: i32) -> VerifyScriptResult {
    let child = Command::new("bash")
        .arg(script)
        .arg(feature_id.to_string())
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

    // Threads leitoras drenam os pipes continuamente — sem isto, um script com saída
    // grande travaria escrevendo no pipe cheio enquanto o loop de poll abaixo só observa
    // o status, sem ler nada (o mesmo problema que `ReadToEndAsync` do .NET e o dreno
    // interno do `subprocess.run(timeout=...)` do Python evitam).
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

// Teto de tempo do verify: uma margem sob o timeout global, para o harness ainda ter
// chance de reportar o estouro antes que a própria guarda de passo do dispatch corte.
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
        "sem limite".to_string()
    } else {
        format!("{timeout_ms}ms")
    }
}

fn write_verify_log(
    target_dir: &Path,
    script: &Path,
    feature_id: i32,
    result: &VerifyScriptResult,
) -> String {
    let relative_dir = ".harness/logs";
    let relative_path: PathBuf = [relative_dir, &format!("verify-feature-{feature_id}.log")]
        .iter()
        .collect();
    let display_path = relative_path.to_string_lossy().replace('\\', "/");

    let full_path = std::env::current_dir()
        .unwrap_or_default()
        .join(&relative_path);
    let write_result = full_path
        .parent()
        .map(std::fs::create_dir_all)
        .unwrap_or(Ok(()))
        .and_then(|_| {
            std::fs::write(
                &full_path,
                format!(
                    "timestampUtc: {}\n\
command: bash ./verify-feature.sh {feature_id}\n\
cwd: {}\n\
script: {}\n\
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
                    script.display(),
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
            "log indisponivel ({})",
            crate::handoff::one_line(&e.to_string(), "")
        ),
    }
}

fn pass_result(feature_id: i32, output: &str, error: &str, log_path: &str) -> String {
    let first_line = first_meaningful_line(&[output, error]);
    let result = if first_line.to_uppercase().starts_with("PASS") {
        snippet(&first_line)
    } else {
        format!("PASS: verify-feature.sh {feature_id} passou")
    };
    result + &log_suffix(log_path)
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
