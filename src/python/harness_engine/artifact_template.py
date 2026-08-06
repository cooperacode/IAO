"""Output template for an artifact: `.harness/skills/<name>/ARTIFACT.md` with `{{key}}`
placeholders replaced by state_store values. The artifact's markdown shape lives
alongside the skill that produces it — outside the code, editable without changing the
package. Pure string substitution: deterministic and zero token.
"""

from __future__ import annotations

from pathlib import Path

from harness_engine import harness_log, path_resolver


def load(skill_name: str) -> str | None:
    """Reads the skill's template; `None` if the skill doesn't define one (the caller decides the fallback)."""
    try:
        path = Path(path_resolver.resolve(str(Path(".harness") / "skills" / skill_name / "ARTIFACT.md")))
        return path.read_text() if path.exists() else None
    except Exception as ex:
        harness_log.error(f"[ArtifactTemplate] failed to read template for {skill_name}: {ex}")
        return None


def render(template: str, values: dict[str, str]) -> str:
    """Replaces each `{{key}}` with its corresponding value. Placeholders with no value
    remain in the text — a visible signal of missing data, not a silent error."""
    result = template
    for key, value in values.items():
        result = result.replace("{{" + key + "}}", value)
    return result
