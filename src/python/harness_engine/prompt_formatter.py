"""Monta o bloco de instrução (input/response/skills) entregue ao modelo."""

from __future__ import annotations

from dataclasses import replace
from pathlib import Path

from harness_engine import path_resolver, state_store
from harness_engine.envelope import Envelope


def skills(*names: str) -> dict[str, str]:
    return {name: str(Path("skills") / name / "SKILL.md") for name in names if name and name.strip()}


def format(input_text: str, output: Envelope, skills_map: dict[str, str] | None = None) -> str:
    # Reinjeta o contexto do driver (capturado no `start`, ver task_registry/state_store)
    # em toda saída — ponto único, para que nenhuma task precise repassá-lo manualmente.
    enriched = output if output.context is not None else replace(output, context=state_store.get_context())

    return (
        "Execute the instruction inside the `input` tag. Then produce your reply as a "
        "SINGLE line of raw JSON matching the schema in the `response` tag, with the "
        "placeholders replaced by real values. Reply with the JSON ONLY: no markdown code "
        f"fences, no comments, no text before or after it. {_read_skills(skills_map)}\n"
        "<input>\n"
        f"    {input_text}\n"
        "</input>\n"
        "<response>\n"
        f"    {enriched.to_json()}\n"
        "</response>"
    )


def _read_skills(skills_map: dict[str, str] | None) -> str:
    if not skills_map:
        return ""

    parts: list[str] = []
    for skill_id, rel_path in skills_map.items():
        if not rel_path or not rel_path.strip():
            continue

        path = Path(path_resolver.resolve(rel_path))
        if not path.exists():
            continue

        content = path.read_text()
        # Inline the content but preserve line breaks as literal "\n" markers
        content = content.replace("\r\n", "\\n").replace("\n", "\\n")

        parts.append(f'<skill id="{skill_id}">\n    {content}\n</skill>')

    if not parts:
        return ""

    body = "".join(parts)
    return f"<skills>\n    {body}\n</skills>"
