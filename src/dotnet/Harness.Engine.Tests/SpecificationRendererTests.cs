using Flows.Specification;

namespace Harness.Engine.Tests;

/// <summary>
/// SpecificationRenderer turns an accepted document into deterministic Markdown (source:
/// specs/0004-blueprint-prd-dotnet.html §4 "JSON aceito → Markdown; nunca o inverso", §9
/// renderer test matrix: "Golden files; ordem estável; escaping; mesma entrada produz mesmos
/// bytes."). The golden fixture below models a small demonstrative TodoApp-style PRD, in the
/// same OBJ-*/MET-*/RSK-* spirit as the historical Project Charter under
/// assets/use-cases/todo-list-greefield/todoapp-webapi-01-project-charter.md.
/// </summary>
public class SpecificationRendererTests
{
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "00-prd.golden.md");

    private static PrdDocument SamplePrd() => new(
        "iao/prd/v1",
        "sha256:idea-todoapp",
        "TodoApp WebAPI gives a single user reliable, self-contained task management over HTTP, with real Postgres persistence and reproducible automated verification.",
        [
            new Goal("OBJ-001", "Functional management: create, list, filter, complete, edit and remove tasks through the WebAPI."),
            new Goal("OBJ-002", "Reliable local persistence: tasks survive an API-only restart."),
        ],
        [
            new Metric("MET-001", "OBJ-001", "endpoints and scenarios in the brief with a passing integration test", "100% covered"),
            new Metric("MET-002", "OBJ-002", "a task's id/title/status after an API-only restart", "unchanged"),
        ],
        ["Migration or compatibility with legacy/JSON data.", "Multiple users, login, authentication or authorization."],
        ["ASP.NET Core Web API in .NET/C#.", "A single local Postgres via Docker Compose."],
        [
            new Risk("RSK-001", "Inconsistent HTTP contract across endpoints.", "Fix casing, status codes and ordering in the SRS/SDD.", "medium"),
        ],
        [
            new Decision("DEC-001", "Vertical Slice Architecture with a minimal shared kernel.", "Keeps each endpoint cohesive and independently testable."),
        ],
        [
            new OpenQuestion("Q-1", "Which pagination *strategy* [if any] applies to `GET /tasks`?", false),
        ]);

    [Fact]
    public void RenderPrd_MesmaEntrada_ProduzMesmosBytesEmDuasChamadas()
    {
        var prd = SamplePrd();

        var first = SpecificationRenderer.RenderPrd(prd);
        var second = SpecificationRenderer.RenderPrd(prd);

        Assert.Equal(first, second);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(first),
            System.Text.Encoding.UTF8.GetBytes(second));
    }

    [Fact]
    public void RenderPrd_BateComFixtureGoldenByteAByte()
    {
        var expected = File.ReadAllText(GoldenPath);

        var rendered = SpecificationRenderer.RenderPrd(SamplePrd());

        Assert.Equal(expected, rendered);
    }

    [Fact]
    public void RenderPrd_EscapaCaracteresEspeciaisDeMarkdown()
    {
        var prd = SamplePrd() with
        {
            Vision = "Uses *asterisks*, _underscores_, `backticks`, [brackets] and a # heading marker.",
        };

        var rendered = SpecificationRenderer.RenderPrd(prd);

        Assert.Contains("\\*asterisks\\*", rendered);
        Assert.Contains("\\_underscores\\_", rendered);
        Assert.Contains("\\`backticks\\`", rendered);
        Assert.Contains("\\[brackets\\]", rendered);
        Assert.Contains("\\# heading", rendered);
    }

    [Fact]
    public void RenderPrd_ColecaoVazia_MantemSecaoComPlaceholder()
    {
        var prd = SamplePrd() with { Risks = [], Decisions = [], OpenQuestions = [] };

        var rendered = SpecificationRenderer.RenderPrd(prd);

        Assert.Contains("## Risks\n_None._", rendered);
        Assert.Contains("## Decisions\n_None._", rendered);
        Assert.Contains("## Open Questions\n_None._", rendered);
    }
}
