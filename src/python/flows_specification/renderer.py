def esc(s):
    # A field can be `None` at runtime even where the accepted document's schema treats it as
    # required text: json.loads() turns a JSON `null` into Python `None`, and dict access here
    # never enforces presence/type the way a statically-typed port's model would. Mirrors the
    # .NET port's SpecificationRenderer.Escape() fix (a production crash there: a null
    # DataRule.rule reached RenderSrs during `approve` and threw). Without this guard,
    # `str(None)` would happily return the *literal text* "None" instead of raising — silent
    # document corruption instead of a loud crash, which is arguably worse. Real gap-closing
    # belongs in the evaluator (reject the document before it's ever accepted); this is the
    # last line of defense so publish never embeds garbage or throws over one empty field.
    if s is None:
        return ""
    return "".join(("\\" + c if c in r"\*_`[]<>#|" else c) for c in str(s))


def bullets(items, fn):
    return "".join(fn(x) + "\n" for x in items) if items else "_None._\n"


def ids(xs):
    return ", ".join(esc(x) for x in xs)


def prd(x):
    return (
        "# Product Requirements Document\n\n## Vision\n"
        + esc(x.get("vision", ""))
        + "\n\n## Goals\n"
        + bullets(
            x.get("goals", []),
            lambda a: f"- **{a.get('id', '')}:** {esc(a.get('statement', ''))}",
        )
        + "\n## Success Metrics\n"
        + bullets(
            x.get("successMetrics", []),
            lambda a: (
                f"- **{a.get('id', '')}** ({esc(a.get('goalId', ''))}): "
                f"{esc(a.get('measure', ''))} → {esc(a.get('target', ''))}"
            ),
        )
        + "\n## Non-Goals\n"
        + bullets(x.get("nonGoals", []), lambda a: "- " + esc(a))
        + "\n## Scope\n"
        + bullets(x.get("scope", []), lambda a: "- " + esc(a))
        + "\n## Risks\n"
        + bullets(
            x.get("risks", []),
            lambda a: (
                f"- **{a.get('id', '')}** ({esc(a.get('severity', ''))}): "
                f"{esc(a.get('description', ''))} — mitigation: {esc(a.get('mitigation', ''))}"
            ),
        )
        + "\n## Decisions\n"
        + bullets(
            x.get("decisions", []),
            lambda a: (
                f"- **{a.get('id', '')}:** {esc(a.get('statement', ''))} — "
                f"rationale: {esc(a.get('rationale', ''))}"
            ),
        )
        + "\n## Open Questions\n"
        + bullets(
            x.get("openQuestions", []),
            lambda a: (
                f"- **{a.get('id', '')}** [{'blocking' if a.get('blocking') else 'non-blocking'}]: "
                f"{esc(a.get('question', ''))}"
            ),
        )
    )


def srs(x):
    out = "# Software Requirements Specification\n\n"
    for title, key in [
        ("Functional Requirements", "functionalRequirements"),
        ("Quality Requirements", "qualityRequirements"),
    ]:
        out += (
            f"## {title}\n"
            + bullets(
                x.get(key, []),
                lambda a: (
                    f"- **{a.get('id', '')}** [{ids(a.get('goalIds', []))}]: {esc(a.get('statement', ''))}"
                ),
            )
            + "\n"
        )
    out += (
        "## Acceptance Criteria\n"
        + bullets(
            x.get("acceptanceCriteria", []),
            lambda a: (
                f"- **{a.get('id', '')}** ({ids(a.get('requirementIds', []))}) — "
                f"Given {esc(a.get('given', ''))}, When {esc(a.get('when', ''))}, Then {esc(a.get('then', ''))}"
            ),
        )
        + "\n## Interfaces\n"
        + bullets(
            x.get("interfaces", []),
            lambda a: (
                f"- **{a.get('id', '')}** ({ids(a.get('requirementIds', []))}) "
                f"{esc(a.get('name', ''))}: {esc(a.get('description', ''))}"
            ),
        )
        + "\n## Data Rules\n"
        + bullets(
            x.get("dataRules", []),
            lambda a: (
                f"- **{a.get('id', '')}** ({ids(a.get('requirementIds', []))}): {esc(a.get('rule', ''))}"
            ),
        )
        + "\n## Delivery\n"
    )
    d = x.get("delivery", {})
    return (
        out
        + f"- **Target:** {esc(d.get('target', ''))}\n- **Verification Strategy:** {esc(d.get('verificationStrategy', ''))}\n- **Bootstrap:** {'yes' if d.get('isBootstrap') else 'no'}\n"
    )


def sdd(x):
    return (
        "# Software Design Document\n\n## Architecture Decision Records\n"
        + bullets(
            x.get("adrs", []),
            lambda a: (
                f"- **{a.get('id', '')}** [{ids(a.get('requirementIds', []))}] "
                f"{esc(a.get('title', ''))}: {esc(a.get('decision', ''))} — rationale: {esc(a.get('rationale', ''))}"
            ),
        )
        + "\n## Controls\n"
        + bullets(
            x.get("controls", []),
            lambda a: (
                f"- **{a.get('id', '')}** [{ids(a.get('requirementIds', []))}] "
                f"{esc(a.get('name', ''))}: {esc(a.get('description', ''))}"
            ),
        )
    )


def readiness(x):
    out = (
        "# Readiness Handoff\n\n## Verdict\n"
        + esc(x.get("verdict", ""))
        + "\n\n## Conflicts\n"
        + bullets(x.get("conflicts", []), lambda a: "- " + esc(a))
        + "\n## Residuals\n"
        + bullets(x.get("residuals", []), lambda a: "- " + esc(a))
        + "\n## Slices\n"
    )
    for s in x.get("slices", []):
        out += (
            f"### {esc(s.get('id', ''))} — {esc(s.get('classification', ''))}\n"
            + "".join(
                f"- **{t}:** {esc(s.get(k, ''))}\n"
                for t, k in [
                    ("Goal", "goal"),
                    ("Observable Outcome", "observableOutcome"),
                    ("Happy Path", "happyPath"),
                    ("Failure Path", "failurePath"),
                    ("Acceptance Criterion", "acceptanceCriterion"),
                    ("Suggested Target", "suggestedTarget"),
                    (
                        "Suggested Verification Strategy",
                        "suggestedVerificationStrategy",
                    ),
                ]
            )
            + f"- **In Scope:** {ids(s.get('inScope', []))}\n- **Out of Scope:** {ids(s.get('outOfScope', []))}\n- **Requirements:** {ids(s.get('requirementIds', []))}\n- **ADRs:** {ids(s.get('adrIds', []))}\n- **Depends On:** {ids(s.get('dependsOn', []))}\n- **Contracts:** {ids(s.get('contracts', []))}\n\n"
        )
    return out
