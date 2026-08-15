"""Command builders for the CLI drivers the GUI can launch in background.

Each driver is handed the flow name ("development" | "specification") and must produce an
argv list that, when run headlessly (no TTY, no interactive approval prompts) from the repo
root, replays exactly the same protocol loop a human operator follows today: read the agent's
own instructions, write `.harness/inbox.json`, invoke `./run-<flow>.sh`, act on `<input>`, and
repeat until `stop`.

Claude resolves `.claude/agents/<flow>.agent.md` natively via `--agent <flow>` — the protocol
text keeps living in exactly one place, we never re-inline it.

Codex CLI (as of codex-cli 0.147.0) has no equivalent `--agent <name>` resolution for
`.codex/agents/<flow>.toml`, so this module reads that file's `developer_instructions` field
(stdlib `tomllib`) and forwards it verbatim as the `codex exec` prompt — still just forwarding
bytes that already exist on disk, never re-deriving the protocol.

Devin has no `--agent <name>`-style resolution either. Its GUI-only "Cascade Workflows"
feature (what `.devin/workflows/<flow>.md` is written for) has no headless invocation path —
confirmed via `docs.devin.ai/desktop/cascade/workflows`: workflows are slash-commands inside
the Devin Desktop app, not something a CLI can run. Cognition does ship a *separate* headless
terminal CLI (`devin`, installed via `curl -fsSL https://cli.devin.ai/install.sh | bash` —
distinct from `devin-desktop`, which is just the IDE's `code`-style window launcher and has no
agent-automation flags at all), so this module reads `.devin/workflows/<flow>.md`, strips its
YAML frontmatter, and forwards the remaining body as the `devin -p` prompt — same
forward-bytes-verbatim approach as Codex, just against a file that happens to be shaped for a
different consumer.

Both Claude and Codex commands were validated by hand against real installs before being
marked `validated: True` below:
- Claude: `claude --help` confirms `-p`, `--agent`, `--permission-mode bypassPermissions`,
  `--output-format stream-json` all exist (v2.1.214).
- Codex: a scratch git repo was spiked with `codex exec "<prompt>" --sandbox workspace-write
  --json` (codex-cli 0.147.0). File writes worked, but `git commit` failed inside that sandbox
  (`.git/index.lock` permission denied — a known friction point between Codex's macOS sandbox
  and git's locking). Switching to `--dangerously-bypass-approvals-and-sandbox` fixed it: shell
  exec and `git commit` both completed cleanly, headlessly, exit code 0. That flag carries the
  same trust level Claude already runs under here (`bypassPermissions`, also no sandbox), which
  is why it's the one used below rather than the narrower `--sandbox workspace-write`.

Devin is `validated: False`: the `devin` terminal CLI isn't installed in the environment this
was built in (only `devin-desktop`, the unrelated IDE launcher, was present) and there's no
`DEVIN_API_KEY`/`COGNITION_API_KEY` to authenticate a real spike. The command below
(`devin -p ... --permission-mode dangerous --respect-workspace-trust false`) is built strictly
from public docs (docs.devin.ai/cli/reference/commands, .../permissions) — plausible, same
shape as the other two, but nobody has actually run it end to end yet. `--respect-workspace-
trust false` matters here specifically: docs.devin.ai's own note says non-interactive `-p` mode
can't show the workspace-trust prompt and just fails in an untrusted directory, which is exactly
what an unattended background run looks like the first time. There's also no `--json`/`--format
json` for `-p` (that flag only exists on `models list`/`list`/`doctor`), so this driver's log
stays plain text — one final response, not per-event NDJSON like Claude/Codex. Flip `validated`
to `True` only after repeating the same kind of spike used for Codex (write a file, run a shell
command, `git commit`, confirm none of it blocks on an approval prompt) against a real
authenticated install.
"""
from __future__ import annotations

import shutil
import json
import os
import re
import tomllib
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
CODEX_AGENTS_DIR = REPO_ROOT / ".codex" / "agents"
DEVIN_WORKFLOWS_DIR = REPO_ROOT / ".devin" / "workflows"
REVISION_REQUEST_PATH = REPO_ROOT / ".harness" / "specification" / "revisions" / "request.json"

# argv[0] to look up on PATH per driver — used by binary_available() so the GUI can tell a
# driver that is "validated" in code (the command shape is known-good) apart from one that
# simply isn't installed on *this* machine (e.g. a package built with --with-gui but no Codex
# CLI on the target host). "devin" here is the separate headless terminal CLI — deliberately
# not "devin-desktop", which is just the IDE window launcher and would give a false positive.
_BINARY_NAMES = {"claude": "claude", "codex": "codex", "devin": "devin"}

DRIVERS = {
    "claude": {
        "label": "Claude Code",
        "validated": True,
        "note": "Usa `claude -p --agent <flow> --permission-mode bypassPermissions --output-format stream-json`.",
    },
    "codex": {
        "label": "Codex CLI",
        "validated": True,
        "note": (
            "Usa `codex exec <developer_instructions de .codex/agents/<flow>.toml> "
            "--dangerously-bypass-approvals-and-sandbox --json`. Mesmo nível de confiança do "
            "driver Claude (sem sandbox, sem prompt de aprovação) — validado manualmente "
            "(escrita de arquivo, shell e git commit) antes de habilitar aqui. A delegação de "
            "subagente para `=== NEW SESSION ===` descrita no .toml não tem um equivalente "
            "nativo confirmado na Codex CLI; o modelo tende a continuar na mesma sessão."
        ),
    },
    "devin": {
        "label": "Devin CLI",
        "validated": False,
        "note": (
            "Usaria `devin -p <corpo de .devin/workflows/<flow>.md, sem o frontmatter> "
            "--permission-mode dangerous --respect-workspace-trust false`. Sem flag de saída "
            "estruturada — `--format json`/`--json` só existem em `models list`/`list`/`doctor`, "
            "não em `-p`, então o log fica em texto plano (uma resposta final, sem NDJSON por "
            "evento como Claude/Codex). NÃO validado: o binário `devin` (CLI de terminal, "
            "separado do app Devin Desktop) não estava instalado no ambiente onde isso foi "
            "construído, e não havia DEVIN_API_KEY/COGNITION_API_KEY para autenticar um teste "
            "real. Comando montado só a partir da documentação pública — confirme com um "
            "spike real (escrever arquivo, rodar shell, git commit, sem travar em aprovação) "
            "antes de habilitar. Também não há delegação de subagente confirmada para o "
            "`=== NEW SESSION ===`."
        ),
    },
}


def _configured_codex_models() -> list[str]:
    config_root = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
    candidates = [config_root / "config.toml", *sorted(config_root.glob("*.config.toml"))]
    models = []
    for path in candidates:
        try:
            text = path.read_text(encoding="utf-8")
        except OSError:
            continue
        for match in re.finditer(r"^\s*model\s*=\s*[\"']([^\"']+)[\"']\s*$", text, re.MULTILINE):
            if match.group(1) not in models:
                models.append(match.group(1))
    return models


def model_options(driver: str) -> list[dict[str, str]]:
    """Return model choices visible in the GUI for a driver.

    The empty value deliberately means "use the driver's configured default". Codex model
    ids are read from local config files when available; extra ids can be supplied through
    HARNESS_CODEX_MODELS for installations that expose additional models through a profile.
    """
    if driver == "codex":
        values = _configured_codex_models()
        values.extend(value.strip() for value in os.environ.get("HARNESS_CODEX_MODELS", "").split(",") if value.strip())
        options = [{"value": "", "label": "Padrão da configuração"}]
        for value in values:
            if not any(option["value"] == value for option in options):
                options.append({"value": value, "label": value})
        return options
    if driver == "claude":
        return [
            {"value": "", "label": "Padrão do Claude"},
            {"value": "sonnet", "label": "Sonnet"},
            {"value": "opus", "label": "Opus"},
            {"value": "haiku", "label": "Haiku"},
        ]
    return [{"value": "", "label": "Padrão do driver"}]


def _model_args(driver: str, model: str | None) -> list[str]:
    value = (model or "").strip()
    if not value:
        return []
    if driver in ("claude", "codex"):
        return ["--model", value]
    return []


def binary_available(driver: str) -> bool:
    """Whether the driver's CLI binary is actually reachable on this machine's PATH."""
    name = _BINARY_NAMES.get(driver)
    return bool(name and shutil.which(name))


def _revision_context(flow: str) -> str:
    if flow != "specification":
        return ""
    approval_gate = (
        "\n\nHUMAN APPROVAL GATE: approval is controlled exclusively by the GUI. Never create, modify, or approve "
        "approval.proposal.json, never send the approve command, and never publish specs/active. If the harness "
        "reaches the approve phase, reports awaiting_approval, or emits an approval instruction, stop the driver "
        "immediately and leave the run awaiting human approval."
    )
    if not REVISION_REQUEST_PATH.is_file():
        return approval_gate
    try:
        request = json.loads(REVISION_REQUEST_PATH.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return approval_gate
    if request.get("status") not in ("pending", "in_progress"):
        return approval_gate
    return (
        "\n\nREVISION REQUEST (one-shot):\n"
        f"Target accepted artifact: {request.get('targetFile', '(unknown)')}\n"
        f"User request: {request.get('request', '')}\n"
        "Before invoking the harness, apply this request only to the target artifact's proposal JSON. "
        "Change text values only: preserve the JSON schema, property names, IDs, array membership, "
        "references, and all unrelated values. Validate that the proposal remains valid JSON. "
        "Then run the harness protocol normally so the target phase and all dependent phases are regenerated. "
        "Do not edit accepted JSON directly."
    ) + approval_gate


def _claude_prompt(flow: str) -> str:
    return (
        f"Start the harness protocol now for the '{flow}' flow and continue turn by turn — "
        "writing the envelope to .harness/inbox.json, running the wrapper script with no "
        "arguments, acting on <input>, and repeating — entirely on your own, without asking for "
        "confirmation, until stdout is exactly 'stop'."
    ) + _revision_context(flow)


def _claude_command(flow: str, model: str | None = None) -> list[str]:
    return [
        "claude",
        *_model_args("claude", model),
        "-p",
        _claude_prompt(flow),
        "--agent",
        flow,
        "--permission-mode",
        "bypassPermissions",
        "--output-format",
        "stream-json",
        "--verbose",
    ]


def _codex_developer_instructions(flow: str) -> str:
    toml_path = CODEX_AGENTS_DIR / f"{flow}.toml"
    if not toml_path.is_file():
        raise ValueError(f"missing Codex agent definition: {toml_path}")
    try:
        data = tomllib.loads(toml_path.read_text(encoding="utf-8"))
    except tomllib.TOMLDecodeError as exc:
        raise ValueError(f"invalid TOML in {toml_path}: {exc}") from exc
    instructions = data.get("developer_instructions")
    if not isinstance(instructions, str) or not instructions.strip():
        raise ValueError(f"{toml_path} has no non-empty 'developer_instructions' field")
    return instructions


def _codex_command(flow: str, model: str | None = None) -> list[str]:
    return [
        "codex",
        "exec",
        *_model_args("codex", model),
        _codex_developer_instructions(flow) + _revision_context(flow),
        "--dangerously-bypass-approvals-and-sandbox",
        "--json",
    ]


def _strip_frontmatter(text: str) -> str:
    """Strips a leading `---\\n...\\n---` YAML block (frontmatter), if present."""
    stripped = text.lstrip()
    if stripped.startswith("---"):
        parts = stripped.split("---", 2)
        if len(parts) >= 3:
            return parts[2].strip()
    return stripped.strip()


def _devin_workflow_body(flow: str) -> str:
    md_path = DEVIN_WORKFLOWS_DIR / f"{flow}.md"
    if not md_path.is_file():
        raise ValueError(f"missing Devin workflow definition: {md_path}")
    body = _strip_frontmatter(md_path.read_text(encoding="utf-8"))
    if not body:
        raise ValueError(f"{md_path} has no content after its frontmatter")
    return body


def _devin_command(flow: str, model: str | None = None) -> list[str]:
    return [
        "devin",
        "-p",
        _devin_workflow_body(flow) + _revision_context(flow),
        "--permission-mode",
        "dangerous",
        # Per docs.devin.ai/cli/reference/commands: "-p mode cannot show the workspace trust
        # prompt, so it fails in an untrusted directory" — exactly the case for an unattended
        # background run that's never been through an interactive trust click. No --json/
        # --format flag exists for -p (checked docs.devin.ai/cli/reference/commands: --format
        # json only exists on `models list`/`list`/`doctor`, not on --print) — the log for this
        # driver stays plain text, one final response, not per-event NDJSON like the other two.
        "--respect-workspace-trust",
        "false",
    ]


_BUILDERS = {
    "claude": _claude_command,
    "codex": _codex_command,
    "devin": _devin_command,
}


def build_command(driver: str, flow: str, model: str | None = None) -> list[str]:
    builder = _BUILDERS.get(driver)
    if builder is None:
        raise ValueError(f"unknown driver '{driver}'")
    if flow not in ("development", "specification"):
        raise ValueError(f"unknown flow '{flow}'")
    return builder(flow, model)
