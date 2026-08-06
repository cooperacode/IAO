"""Assembles the instruction block (input/response/skills) delivered to the model."""

from __future__ import annotations

from dataclasses import replace
from pathlib import Path

from harness_engine import path_resolver, state_store
from harness_engine.envelope import Envelope


def skills(*names: str) -> dict[str, str]:
    return {name: str(Path(".harness") / "skills" / name / "SKILL.md") for name in names if name and name.strip()}


def format(input_text: str, output: Envelope, skills_map: dict[str, str] | None = None) -> str:
    # Reinjects the driver context (captured on `start`, see task_registry/state_store)
    # into every output — a single point, so no task needs to pass it along manually.
    enriched = output if output.context is not None else replace(output, context=state_store.get_context())

    return (
        "Execute the instruction inside the `input` tag. Then reply with the result as JSON.\n"
        "\n"
        "Output contract — a reply that breaks any of these rules is invalid and wastes a retry:\n"
        "1. Output EXACTLY one JSON object, on a SINGLE line, matching the shape in the "
        "`response` tag with the placeholders replaced by real values.\n"
        "2. The object is the ONLY thing you output: no markdown code fences, no comments, "
        "no prose before or after it, nothing.\n"
        "3. Keep the same keys, types and nesting as the schema — do not add, remove, "
        "rename fields, or wrap the object in an array.\n"
        "4. Every value must be valid JSON: use only double quotes for strings, escape `\"` "
        "and `\\` inside them, and replace any line break inside a value with the literal "
        "characters `\\n` — never a raw newline. No trailing commas.\n"
        "5. Before answering, mentally re-parse your own output as JSON; if it would fail "
        f"to parse, fix it before sending.\n"
        f"\n{_read_skills(skills_map)}\n"
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

        parts.append(f'<skill id="{skill_id}">\n    {inline(path.read_text())}\n</skill>')

    if not parts:
        return ""

    body = "".join(parts)
    return f"<skills>\n    {body}\n</skills>"


def inline(content: str) -> str:
    """Collapses content onto a single line for embedding inside an XML-ish tag,
    preserving line breaks as literal "\\n" markers instead of real newlines — so a
    multi-line artifact (a skill file, a brief) doesn't break the tag's one-line shape in
    the emitted prompt."""
    return content.replace("\r\n", "\\n").replace("\n", "\\n")
