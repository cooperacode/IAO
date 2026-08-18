//! Renderer (mirrors `SpecificationRenderer.cs`): accepted JSON -> deterministic Markdown.

use crate::publisher::FILENAMES;
use serde_json::Value;
use std::collections::HashMap;

/// Backslash-escapes Markdown special characters in free text. Treats a missing/non-string
/// value as an empty string rather than panicking — the last line of defense against a
/// structurally-odd (but somehow accepted) document, same role as .NET's `Escape(string?)`.
pub(crate) fn escape_md(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for c in text.chars() {
        if matches!(
            c,
            '\\' | '*' | '_' | '`' | '[' | ']' | '<' | '>' | '#' | '|'
        ) {
            out.push('\\');
        }
        out.push(c);
    }
    out
}
fn text_of(v: &Value) -> &str {
    v.as_str().unwrap_or("")
}
fn escaped(v: &Value) -> String {
    escape_md(text_of(v))
}
/// Joins a list of ids for inline display, escaping each id individually — mirrors `JoinIds`.
fn join_ids(v: &Value) -> String {
    v.as_array()
        .into_iter()
        .flatten()
        .map(escaped)
        .collect::<Vec<_>>()
        .join(", ")
}
fn items_of<'a>(v: &'a Value, key: &str) -> &'a [Value] {
    v.get(key)
        .and_then(Value::as_array)
        .map_or(&[], |a| a.as_slice())
}
/// Renders each item in array order, or "_None._" when the list is empty — a section's
/// heading structure never shifts based on content. Mirrors `AppendBulletsOrNone`.
fn append_bullets_or_none<F: Fn(&Value) -> String>(
    sb: &mut String,
    list: &[Value],
    render: F,
    trailing_blank_line: bool,
) {
    if list.is_empty() {
        sb.push_str("_None._\n");
    } else {
        for item in list {
            sb.push_str(&render(item));
            sb.push('\n');
        }
    }
    if trailing_blank_line {
        sb.push('\n');
    }
}

/// Renders an accepted PRD as `00-prd.md`.
pub(crate) fn render_prd(prd: &Value) -> String {
    let mut sb = String::new();
    sb.push_str("# Product Requirements Document\n\n");

    sb.push_str("## Vision\n");
    sb.push_str(&escaped(&prd["vision"]));
    sb.push_str("\n\n");

    sb.push_str("## Goals\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "goals"),
        |g| format!("- **{}:** {}", text_of(&g["id"]), escaped(&g["statement"])),
        true,
    );

    sb.push_str("## Success Metrics\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "successMetrics"),
        |m| {
            format!(
                "- **{}** ({}): {} → {}",
                text_of(&m["id"]),
                escaped(&m["goalId"]),
                escaped(&m["measure"]),
                escaped(&m["target"])
            )
        },
        true,
    );

    sb.push_str("## Non-Goals\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "nonGoals"),
        |ng| format!("- {}", escaped(ng)),
        true,
    );

    sb.push_str("## Scope\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "scope"),
        |s| format!("- {}", escaped(s)),
        true,
    );

    sb.push_str("## Risks\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "risks"),
        |r| {
            format!(
                "- **{}** ({}): {} — mitigation: {}",
                text_of(&r["id"]),
                escaped(&r["severity"]),
                escaped(&r["description"]),
                escaped(&r["mitigation"])
            )
        },
        true,
    );

    sb.push_str("## Decisions\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "decisions"),
        |d| {
            format!(
                "- **{}:** {} — rationale: {}",
                text_of(&d["id"]),
                escaped(&d["statement"]),
                escaped(&d["rationale"])
            )
        },
        true,
    );

    sb.push_str("## Open Questions\n");
    append_bullets_or_none(
        &mut sb,
        items_of(prd, "openQuestions"),
        |q| {
            let blocking = q["blocking"].as_bool().unwrap_or(false);
            format!(
                "- **{}** [{}]: {}",
                text_of(&q["id"]),
                if blocking { "blocking" } else { "non-blocking" },
                escaped(&q["question"])
            )
        },
        false,
    );

    sb
}

/// Renders an accepted SRS as `10-software-requirements-specification.md`.
pub(crate) fn render_srs(srs: &Value) -> String {
    let mut sb = String::new();
    sb.push_str("# Software Requirements Specification\n\n");

    sb.push_str("## Functional Requirements\n");
    append_bullets_or_none(
        &mut sb,
        items_of(srs, "functionalRequirements"),
        |r| {
            format!(
                "- **{}** [{}]: {}",
                text_of(&r["id"]),
                join_ids(&r["goalIds"]),
                escaped(&r["statement"])
            )
        },
        true,
    );

    sb.push_str("## Quality Requirements\n");
    append_bullets_or_none(
        &mut sb,
        items_of(srs, "qualityRequirements"),
        |r| {
            format!(
                "- **{}** [{}]: {}",
                text_of(&r["id"]),
                join_ids(&r["goalIds"]),
                escaped(&r["statement"])
            )
        },
        true,
    );

    sb.push_str("## Acceptance Criteria\n");
    append_bullets_or_none(
        &mut sb,
        items_of(srs, "acceptanceCriteria"),
        |a| {
            format!(
                "- **{}** ({}) — Given {}, When {}, Then {}",
                text_of(&a["id"]),
                join_ids(&a["requirementIds"]),
                escaped(&a["given"]),
                escaped(&a["when"]),
                escaped(&a["then"])
            )
        },
        true,
    );

    sb.push_str("## Interfaces\n");
    append_bullets_or_none(
        &mut sb,
        items_of(srs, "interfaces"),
        |i| {
            format!(
                "- **{}** ({}) {}: {}",
                text_of(&i["id"]),
                join_ids(&i["requirementIds"]),
                escaped(&i["name"]),
                escaped(&i["description"])
            )
        },
        true,
    );

    sb.push_str("## Data Rules\n");
    append_bullets_or_none(
        &mut sb,
        items_of(srs, "dataRules"),
        |d| {
            format!(
                "- **{}** ({}): {}",
                text_of(&d["id"]),
                join_ids(&d["requirementIds"]),
                escaped(&d["rule"])
            )
        },
        true,
    );

    sb.push_str("## Delivery\n");
    let delivery = &srs["delivery"];
    sb.push_str(&format!("- **Target:** {}\n", escaped(&delivery["target"])));
    sb.push_str(&format!(
        "- **Verification Strategy:** {}\n",
        escaped(&delivery["verificationStrategy"])
    ));
    sb.push_str(&format!(
        "- **Bootstrap:** {}\n",
        if delivery["isBootstrap"].as_bool().unwrap_or(false) {
            "yes"
        } else {
            "no"
        }
    ));

    sb
}

/// Renders an accepted SDD as `20-software-design-document.md`.
pub(crate) fn render_sdd(sdd: &Value) -> String {
    let mut sb = String::new();
    sb.push_str("# Software Design Document\n\n");

    if let Some(content) = sdd["designContent"].as_str() {
        if !content.trim().is_empty() {
            sb.push_str(content.trim_end());
            sb.push_str("\n\n");
        }
    }

    sb.push_str("## Architecture Decision Records\n");
    append_bullets_or_none(
        &mut sb,
        items_of(sdd, "adrs"),
        |a| {
            format!(
                "- **{}** [{}] {}: {} — rationale: {}",
                text_of(&a["id"]),
                join_ids(&a["requirementIds"]),
                escaped(&a["title"]),
                escaped(&a["decision"]),
                escaped(&a["rationale"])
            )
        },
        true,
    );

    sb.push_str("## Controls\n");
    append_bullets_or_none(
        &mut sb,
        items_of(sdd, "controls"),
        |c| {
            format!(
                "- **{}** [{}] {}: {}",
                text_of(&c["id"]),
                join_ids(&c["requirementIds"]),
                escaped(&c["name"]),
                escaped(&c["description"])
            )
        },
        false,
    );

    sb
}

/// Renders an accepted readiness verdict as `30-readiness-handoff.md`.
pub(crate) fn render_readiness(verdict: &Value) -> String {
    let mut sb = String::new();
    sb.push_str("# Readiness Handoff\n\n");

    sb.push_str("## Verdict\n");
    sb.push_str(&escaped(&verdict["verdict"]));
    sb.push_str("\n\n");

    sb.push_str("## Conflicts\n");
    append_bullets_or_none(
        &mut sb,
        items_of(verdict, "conflicts"),
        |c| format!("- {}", escaped(c)),
        true,
    );

    sb.push_str("## Residuals\n");
    append_bullets_or_none(
        &mut sb,
        items_of(verdict, "residuals"),
        |r| format!("- {}", escaped(r)),
        true,
    );

    sb.push_str("## Slices\n");
    let slices = items_of(verdict, "slices");
    if slices.is_empty() {
        sb.push_str("_None._\n");
    } else {
        for slice in slices {
            sb.push_str(&format!(
                "### {} — {}\n",
                escaped(&slice["id"]),
                escaped(&slice["classification"])
            ));
            sb.push_str(&format!("- **Goal:** {}\n", escaped(&slice["goal"])));
            sb.push_str(&format!(
                "- **In Scope:** {}\n",
                join_ids(&slice["inScope"])
            ));
            sb.push_str(&format!(
                "- **Out of Scope:** {}\n",
                join_ids(&slice["outOfScope"])
            ));
            sb.push_str(&format!(
                "- **Observable Outcome:** {}\n",
                escaped(&slice["observableOutcome"])
            ));
            sb.push_str(&format!(
                "- **Requirements:** {}\n",
                join_ids(&slice["requirementIds"])
            ));
            sb.push_str(&format!("- **ADRs:** {}\n", join_ids(&slice["adrIds"])));
            sb.push_str(&format!(
                "- **Depends On:** {}\n",
                join_ids(&slice["dependsOn"])
            ));
            sb.push_str(&format!(
                "- **Contracts:** {}\n",
                join_ids(&slice["contracts"])
            ));
            sb.push_str(&format!(
                "- **Happy Path:** {}\n",
                escaped(&slice["happyPath"])
            ));
            sb.push_str(&format!(
                "- **Failure Path:** {}\n",
                escaped(&slice["failurePath"])
            ));
            sb.push_str(&format!(
                "- **Acceptance Criterion:** {}\n",
                escaped(&slice["acceptanceCriterion"])
            ));
            sb.push_str(&format!(
                "- **Suggested Target:** {}\n",
                escaped(&slice["suggestedTarget"])
            ));
            sb.push_str(&format!(
                "- **Suggested Verification Strategy:** {}\n",
                escaped(&slice["suggestedVerificationStrategy"])
            ));
            sb.push('\n');
        }
    }

    sb
}

/// Renders the four accepted documents into their published filenames, without touching disk
/// — mirrors `SpecificationPublisher.RenderAll`, reused both by the pre-publish byte-budget
/// gate and by `publish()` itself so they never diverge.
pub(crate) fn render_all(
    prd: &Value,
    srs: &Value,
    sdd: &Value,
    readiness: &Value,
) -> HashMap<String, String> {
    let mut m = HashMap::new();
    m.insert(FILENAMES[0].to_string(), render_prd(prd));
    m.insert(FILENAMES[1].to_string(), render_srs(srs));
    m.insert(FILENAMES[2].to_string(), render_sdd(sdd));
    m.insert(FILENAMES[3].to_string(), render_readiness(readiness));
    m
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde_json::json;

    #[test]
    fn escape_md_escapa_todos_os_caracteres_especiais() {
        let escaped = escape_md("a*b_c`d[e]f<g>h#i|j\\k");
        assert_eq!(escaped, "a\\*b\\_c\\`d\\[e\\]f\\<g\\>h\\#i\\|j\\\\k");
    }

    #[test]
    fn escape_md_trata_texto_vazio_sem_falhar() {
        assert_eq!(escape_md(""), "");
    }

    #[test]
    fn render_prd_lista_vazia_renderiza_none_e_nunca_omite_secao() {
        let prd = json!({
            "vision": "the vision",
            "goals": [],
            "successMetrics": [],
            "nonGoals": [],
            "scope": [],
            "risks": [],
            "decisions": [],
            "openQuestions": []
        });

        let rendered = render_prd(&prd);

        assert!(rendered.starts_with("# Product Requirements Document\n\n"));
        assert!(rendered.contains("## Vision\nthe vision\n\n"));
        for heading in [
            "## Goals",
            "## Success Metrics",
            "## Non-Goals",
            "## Scope",
            "## Risks",
            "## Decisions",
            "## Open Questions",
        ] {
            assert!(rendered.contains(heading), "missing heading {heading}");
        }
        assert!(
            rendered.matches("_None._").count() == 7,
            "expected 7 empty sections, got: {rendered}"
        );
    }

    #[test]
    fn render_prd_escapa_markdown_em_texto_livre_mas_nao_no_id() {
        let prd = json!({
            "vision": "v",
            "goals": [{"id": "G*1", "statement": "do *this*"}],
            "successMetrics": [],
            "nonGoals": [],
            "scope": [],
            "risks": [],
            "decisions": [],
            "openQuestions": []
        });

        let rendered = render_prd(&prd);

        // The id itself is NOT escaped (mirrors the .NET renderer's raw `{g.Id}`)...
        assert!(rendered.contains("**G*1:**"));
        // ...but free text IS escaped.
        assert!(rendered.contains("do \\*this\\*"));
    }

    #[test]
    fn render_prd_valor_null_vira_string_vazia_sem_panico() {
        let prd = json!({
            "vision": null,
            "goals": [{"id": "G-1", "statement": null}],
            "successMetrics": [],
            "nonGoals": [],
            "scope": [],
            "risks": [],
            "decisions": [],
            "openQuestions": []
        });

        let rendered = render_prd(&prd);

        assert!(rendered.contains("**G-1:** \n"));
    }

    #[test]
    fn render_readiness_slices_vazio_usa_none() {
        let verdict = json!({"verdict": "READY", "conflicts": [], "residuals": [], "slices": []});

        let rendered = render_readiness(&verdict);

        assert!(rendered.contains("## Slices\n_None._\n"));
    }

    #[test]
    fn render_sdd_preserva_markdown_de_design() {
        let sdd = json!({
            "schema": "iao/sdd/v1",
            "srsDigest": "sha256:srs",
            "designContent": "## Architecture\n\n```mermaid\ngraph TD\n```\n\n```text\napp/\n```",
            "adrs": [],
            "controls": []
        });

        let rendered = render_sdd(&sdd);

        assert!(rendered.contains("```mermaid\ngraph TD\n```"));
        assert!(rendered.contains("app/"));
    }

    fn slice(id: &str, depends_on: &[&str]) -> Value {
        json!({
            "id": id,
            "classification": "c",
            "goal": "g",
            "inScope": [],
            "outOfScope": [],
            "observableOutcome": "o",
            "requirementIds": [],
            "adrIds": [],
            "dependsOn": depends_on,
            "contracts": [],
            "happyPath": "h",
            "failurePath": "f",
            "acceptanceCriterion": "a",
            "suggestedTarget": "t",
            "suggestedVerificationStrategy": "v"
        })
    }

    #[test]
    fn render_readiness_slice_inclui_todos_os_15_campos() {
        let verdict = json!({
            "verdict": "READY",
            "conflicts": [],
            "residuals": [],
            "slices": [slice("SL-1", &[])]
        });

        let rendered = render_readiness(&verdict);

        for label in [
            "Goal",
            "In Scope",
            "Out of Scope",
            "Observable Outcome",
            "Requirements",
            "ADRs",
            "Depends On",
            "Contracts",
            "Happy Path",
            "Failure Path",
            "Acceptance Criterion",
            "Suggested Target",
            "Suggested Verification Strategy",
        ] {
            assert!(
                rendered.contains(&format!("**{label}:**")),
                "missing field {label}"
            );
        }
        assert!(rendered.contains("### SL-1 — c\n"));
    }
}
