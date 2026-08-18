using System.Linq;
using System.Text;

namespace Flows.Specification;

/// <summary>
/// Renders <em>accepted</em> Specification documents into deterministic Markdown — never
/// the reverse direction (blueprint 0004 §4: "JSON aceito → Markdown; nunca o inverso").
/// Every render method here is a pure function of its input record: the same value always
/// produces the same UTF-8 bytes, so two renders of the same accepted document are
/// byte-identical and a golden-file diff is meaningful. Callers are responsible for only
/// ever passing an <c>accepted</c> document (never a <c>proposal</c>) — the renderer has no
/// way to enforce that itself, since by the time it receives a <see cref="PrdDocument"/> the
/// distinction has already been erased by the type system.
/// </summary>
public static class SpecificationRenderer
{
    /// <summary>Renders an accepted <see cref="PrdDocument"/> as <c>00-prd.md</c>.</summary>
    public static string RenderPrd(PrdDocument prd)
    {
        var sb = new StringBuilder();

        sb.Append("# Product Requirements Document\n\n");

        sb.Append("## Vision\n");
        sb.Append(Escape(prd.Vision)).Append("\n\n");

        sb.Append("## Goals\n");
        AppendBulletsOrNone(sb, prd.Goals, g => $"- **{g.Id}:** {Escape(g.Statement)}");

        sb.Append("## Success Metrics\n");
        AppendBulletsOrNone(sb, prd.SuccessMetrics, m => $"- **{m.Id}** ({Escape(m.GoalId)}): {Escape(m.Measure)} → {Escape(m.Target)}");

        sb.Append("## Non-Goals\n");
        AppendBulletsOrNone(sb, prd.NonGoals, ng => $"- {Escape(ng)}");

        sb.Append("## Scope\n");
        AppendBulletsOrNone(sb, prd.Scope, s => $"- {Escape(s)}");

        sb.Append("## Risks\n");
        AppendBulletsOrNone(sb, prd.Risks, r => $"- **{r.Id}** ({Escape(r.Severity)}): {Escape(r.Description)} — mitigation: {Escape(r.Mitigation)}");

        sb.Append("## Decisions\n");
        AppendBulletsOrNone(sb, prd.Decisions, d => $"- **{d.Id}:** {Escape(d.Statement)} — rationale: {Escape(d.Rationale)}");

        sb.Append("## Open Questions\n");
        AppendBulletsOrNone(sb, prd.OpenQuestions, q => $"- **{q.Id}** [{(q.Blocking ? "blocking" : "non-blocking")}]: {Escape(q.Question)}", trailingBlankLine: false);

        return sb.ToString();
    }

    /// <summary>Renders an accepted <see cref="SoftwareSpecification"/> as <c>10-software-requirements-specification.md</c>.</summary>
    public static string RenderSrs(SoftwareSpecification srs)
    {
        var sb = new StringBuilder();

        sb.Append("# Software Requirements Specification\n\n");

        sb.Append("## Functional Requirements\n");
        AppendBulletsOrNone(sb, srs.FunctionalRequirements, r => $"- **{r.Id}** [{JoinIds(r.GoalIds)}]: {Escape(r.Statement)}");

        sb.Append("## Quality Requirements\n");
        AppendBulletsOrNone(sb, srs.QualityRequirements, r => $"- **{r.Id}** [{JoinIds(r.GoalIds)}]: {Escape(r.Statement)}");

        sb.Append("## Acceptance Criteria\n");
        AppendBulletsOrNone(sb, srs.AcceptanceCriteria, a => $"- **{a.Id}** ({JoinIds(a.RequirementIds)}) — Given {Escape(a.Given)}, When {Escape(a.When)}, Then {Escape(a.Then)}");

        sb.Append("## Interfaces\n");
        AppendBulletsOrNone(sb, srs.Interfaces, i => $"- **{i.Id}** ({JoinIds(i.RequirementIds)}) {Escape(i.Name)}: {Escape(i.Description)}");

        sb.Append("## Data Rules\n");
        AppendBulletsOrNone(sb, srs.DataRules, d => $"- **{d.Id}** ({JoinIds(d.RequirementIds)}): {Escape(d.Rule)}");

        sb.Append("## Delivery\n");
        sb.Append($"- **Target:** {Escape(srs.Delivery.Target)}\n");
        sb.Append($"- **Verification Strategy:** {Escape(srs.Delivery.VerificationStrategy)}\n");
        sb.Append($"- **Bootstrap:** {(srs.Delivery.IsBootstrap ? "yes" : "no")}\n");

        return sb.ToString();
    }

    /// <summary>Renders an accepted <see cref="SoftwareDesignDocument"/> as <c>20-software-design-document.md</c>.</summary>
    public static string RenderSdd(SoftwareDesignDocument sdd)
    {
        var sb = new StringBuilder();

        sb.Append("# Software Design Document\n\n");

        if (!string.IsNullOrWhiteSpace(sdd.DesignContent))
        {
            // This is intentionally raw Markdown: escaping it would destroy Mermaid
            // diagrams, fenced code blocks, tables and folder trees that the SDD is meant to
            // preserve from source-backed design context.
            sb.Append(sdd.DesignContent.TrimEnd()).Append("\n\n");
        }

        sb.Append("## Architecture Decision Records\n");
        AppendBulletsOrNone(sb, sdd.Adrs, a => $"- **{a.Id}** [{JoinIds(a.RequirementIds)}] {Escape(a.Title)}: {Escape(a.Decision)} — rationale: {Escape(a.Rationale)}");

        sb.Append("## Controls\n");
        AppendBulletsOrNone(sb, sdd.Controls, c => $"- **{c.Id}** [{JoinIds(c.RequirementIds)}] {Escape(c.Name)}: {Escape(c.Description)}", trailingBlankLine: false);

        return sb.ToString();
    }

    /// <summary>Renders an accepted <see cref="ReadinessVerdict"/> as <c>30-readiness-handoff.md</c>.</summary>
    public static string RenderReadiness(ReadinessVerdict verdict)
    {
        var sb = new StringBuilder();

        sb.Append("# Readiness Handoff\n\n");

        sb.Append("## Verdict\n");
        sb.Append(Escape(verdict.Verdict)).Append("\n\n");

        sb.Append("## Conflicts\n");
        AppendBulletsOrNone(sb, verdict.Conflicts, c => $"- {Escape(c)}");

        sb.Append("## Residuals\n");
        AppendBulletsOrNone(sb, verdict.Residuals, r => $"- {Escape(r)}");

        sb.Append("## Slices\n");
        if (verdict.Slices.Length == 0)
        {
            sb.Append("_None._\n");
        }
        else
        {
            foreach (var slice in verdict.Slices)
            {
                sb.Append($"### {Escape(slice.Id)} — {Escape(slice.Classification)}\n");
                sb.Append($"- **Goal:** {Escape(slice.Goal)}\n");
                sb.Append($"- **In Scope:** {JoinIds(slice.InScope)}\n");
                sb.Append($"- **Out of Scope:** {JoinIds(slice.OutOfScope)}\n");
                sb.Append($"- **Observable Outcome:** {Escape(slice.ObservableOutcome)}\n");
                sb.Append($"- **Requirements:** {JoinIds(slice.RequirementIds)}\n");
                sb.Append($"- **ADRs:** {JoinIds(slice.AdrIds)}\n");
                sb.Append($"- **Depends On:** {JoinIds(slice.DependsOn)}\n");
                sb.Append($"- **Contracts:** {JoinIds(slice.Contracts)}\n");
                sb.Append($"- **Happy Path:** {Escape(slice.HappyPath)}\n");
                sb.Append($"- **Failure Path:** {Escape(slice.FailurePath)}\n");
                sb.Append($"- **Acceptance Criterion:** {Escape(slice.AcceptanceCriterion)}\n");
                sb.Append($"- **Suggested Target:** {Escape(slice.SuggestedTarget)}\n");
                sb.Append($"- **Suggested Verification Strategy:** {Escape(slice.SuggestedVerificationStrategy)}\n");
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    // Renders each item in array order (the order the accepted JSON already carries) so the
    // output is a pure function of the input — no re-sorting that could disagree with what a
    // human reviewer sees in the source document. "_None._" keeps every section present even
    // when the underlying list happens to be empty, so the heading structure never shifts.
    private static void AppendBulletsOrNone<T>(StringBuilder sb, IReadOnlyList<T> items, Func<T, string> render, bool trailingBlankLine = true)
    {
        if (items.Count == 0)
            sb.Append("_None._\n");
        else
            foreach (var item in items)
                sb.Append(render(item)).Append('\n');

        if (trailingBlankLine)
            sb.Append('\n');
    }

    // Joins a list of ids for inline display (e.g. "[G-1, G-2]"), escaping each id individually
    // so a pathological id can't smuggle in Markdown syntax any more than free text can.
    private static string JoinIds(IReadOnlyList<string> ids) => string.Join(", ", ids.Select(Escape));

    // Backslash-escapes the Markdown special characters that free-text PRD fields could
    // contain, so a title/description with e.g. "*bold*" or "[link](x)" in it renders as
    // literal text instead of being interpreted as Markdown syntax.
    //
    // `text` is declared non-nullable, but System.Text.Json happily assigns null to a
    // non-nullable reference-typed record property when the source JSON has that field as
    // `null` (or omits it) — nullable reference types are compile-time-only and are not
    // enforced by deserialization. An accepted document with e.g. a DataRule whose "rule" is
    // null therefore reaches this method at render/publish time, well past the evaluator gate.
    // The evaluators should reject that kind of document before it is ever accepted (see
    // SpecificationEvaluator.EvaluateSrs's *_TEXT_MISSING checks), but this is the last line of
    // defense: a renderer must never take down the whole harness turn over one empty field in
    // an otherwise-valid document, so treat a null the same as an empty string instead of
    // letting `text.Length` throw a NullReferenceException.
    private static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']' or '<' or '>' or '#' or '|')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
