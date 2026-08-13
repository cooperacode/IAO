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

    // Backslash-escapes the Markdown special characters that free-text PRD fields could
    // contain, so a title/description with e.g. "*bold*" or "[link](x)" in it renders as
    // literal text instead of being interpreted as Markdown syntax.
    private static string Escape(string text)
    {
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
