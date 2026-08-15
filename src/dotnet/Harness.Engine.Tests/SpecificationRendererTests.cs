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

    // ---- RenderSrs (10-software-requirements-specification.md) ----

    private static SoftwareSpecification SampleSrs() => new(
        "iao/srs/v1",
        "sha256:prd-todoapp",
        [new Requirement("RF-001", ["OBJ-001"], "Create, list, filter, complete, edit and remove tasks.", [], ["AC-001"])],
        [new Requirement("RNF-001", ["OBJ-002"], "Persist tasks across an API-only restart.", ["RF-001"], ["AC-002"])],
        [
            new AcceptanceCriterion("AC-001", ["RF-001"], "a task exists", "it is listed", "it appears in the response"),
            new AcceptanceCriterion("AC-002", ["RNF-001"], "the API restarts", "a task is read back", "its fields are unchanged"),
        ],
        [new InterfaceContract("IF-001", ["RF-001"], "Tasks API", "REST endpoints for task management.")],
        [new DataRule("DR-001", ["RF-001"], "A task id is unique within the store.")],
        new DeliveryContract("ASP.NET Core Web API", "integration tests against a local Postgres", true));

    [Fact]
    public void RenderSrs_MesmaEntrada_ProduzMesmosBytesEmDuasChamadas()
    {
        var srs = SampleSrs();

        var first = SpecificationRenderer.RenderSrs(srs);
        var second = SpecificationRenderer.RenderSrs(srs);

        Assert.Equal(first, second);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(first),
            System.Text.Encoding.UTF8.GetBytes(second));
    }

    [Fact]
    public void RenderSrs_ContemRequisitosECriteriosDeAceitacao()
    {
        var rendered = SpecificationRenderer.RenderSrs(SampleSrs());

        Assert.Contains("## Functional Requirements", rendered);
        Assert.Contains("**RF-001**", rendered);
        Assert.Contains("## Quality Requirements", rendered);
        Assert.Contains("**RNF-001**", rendered);
        Assert.Contains("## Acceptance Criteria", rendered);
        Assert.Contains("**AC-001**", rendered);
    }

    [Fact]
    public void RenderSrs_EscapaCaracteresEspeciaisDeMarkdown()
    {
        var srs = SampleSrs() with
        {
            FunctionalRequirements = [new Requirement("RF-001", ["OBJ-001"], "Uses *asterisks* and [brackets].", [], ["AC-001"])],
        };

        var rendered = SpecificationRenderer.RenderSrs(srs);

        Assert.Contains("\\*asterisks\\*", rendered);
        Assert.Contains("\\[brackets\\]", rendered);
    }

    [Fact]
    public void RenderSrs_ColecaoVazia_MantemSecaoComPlaceholder()
    {
        var srs = SampleSrs() with { Interfaces = [], DataRules = [] };

        var rendered = SpecificationRenderer.RenderSrs(srs);

        Assert.Contains("## Interfaces\n_None._", rendered);
        Assert.Contains("## Data Rules\n_None._", rendered);
    }

    // Regression test for a real production crash: a `DataRule.Rule` that was `null` at
    // runtime (System.Text.Json assigns null to a non-nullable string property when the
    // source JSON has that field as `null`, since nullable reference types are not enforced
    // by deserialization) reached RenderSrs during `approve`/publish and threw a
    // NullReferenceException inside Escape(), which the harness recorded as a "fault" and then
    // refused every subsequent turn for that run. The evaluator should reject a document like
    // this before it is ever accepted (see SpecificationEvaluatorTests' *_TEXT_MISSING cases),
    // but the renderer itself must never crash the whole harness turn over one empty field —
    // this asserts the null is rendered as empty text instead of thrown.
    [Fact]
    public void RenderSrs_CampoDeTextoNulo_NaoLancaExcecao()
    {
        var srs = SampleSrs() with
        {
            DataRules = [new DataRule("DR-001", ["RF-001"], null!)],
        };

        var rendered = SpecificationRenderer.RenderSrs(srs);

        Assert.Contains("**DR-001**", rendered);
    }

    // ---- RenderSdd (20-software-design-document.md) ----

    private static SoftwareDesignDocument SampleSdd() => new(
        "iao/sdd/v1",
        "sha256:srs-todoapp",
        [new Adr("ADR-001", "Vertical Slice Architecture", "Organize the API by feature slice.", "Keeps each endpoint cohesive.", ["RF-001", "RNF-001"])],
        [new InterfaceControl("IC-001", "Input validation", "Reject malformed task payloads at the boundary.", ["RF-001"])]);

    [Fact]
    public void RenderSdd_MesmaEntrada_ProduzMesmosBytesEmDuasChamadas()
    {
        var sdd = SampleSdd();

        var first = SpecificationRenderer.RenderSdd(sdd);
        var second = SpecificationRenderer.RenderSdd(sdd);

        Assert.Equal(first, second);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(first),
            System.Text.Encoding.UTF8.GetBytes(second));
    }

    [Fact]
    public void RenderSdd_ContemAdrsEControles()
    {
        var rendered = SpecificationRenderer.RenderSdd(SampleSdd());

        Assert.Contains("## Architecture Decision Records", rendered);
        Assert.Contains("**ADR-001**", rendered);
        Assert.Contains("## Controls", rendered);
        Assert.Contains("**IC-001**", rendered);
    }

    [Fact]
    public void RenderSdd_EscapaCaracteresEspeciaisDeMarkdown()
    {
        var sdd = SampleSdd() with
        {
            Adrs = [new Adr("ADR-001", "Uses `backticks` and _underscores_.", "decision", "rationale", ["RF-001"])],
        };

        var rendered = SpecificationRenderer.RenderSdd(sdd);

        Assert.Contains("\\`backticks\\`", rendered);
        Assert.Contains("\\_underscores\\_", rendered);
    }

    [Fact]
    public void RenderSdd_ColecaoVazia_MantemSecaoComPlaceholder()
    {
        var sdd = SampleSdd() with { Controls = [] };

        var rendered = SpecificationRenderer.RenderSdd(sdd);

        Assert.Contains("## Controls\n_None._", rendered);
    }

    // ---- RenderReadiness (30-readiness-handoff.md) ----

    private static ReadinessVerdict SampleReadiness() => new(
        "READY",
        [
            new ReadinessSlice(
                "SL-001", "core", "Create and list tasks.",
                ["create a task", "list tasks"], ["editing a task"],
                "a created task appears in the list",
                ["RF-001"], ["ADR-001"], [], ["Tasks API"],
                "user adds a task and sees it listed", "invalid input is rejected",
                "the task appears in the response", "ASP.NET Core Web API",
                "integration tests against a local Postgres"),
        ],
        ["Pagination strategy is still open."],
        ["Legacy data import is out of scope."]);

    [Fact]
    public void RenderReadiness_MesmaEntrada_ProduzMesmosBytesEmDuasChamadas()
    {
        var readiness = SampleReadiness();

        var first = SpecificationRenderer.RenderReadiness(readiness);
        var second = SpecificationRenderer.RenderReadiness(readiness);

        Assert.Equal(first, second);
        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(first),
            System.Text.Encoding.UTF8.GetBytes(second));
    }

    [Fact]
    public void RenderReadiness_ContemVerdictConflitosResiduosEFatias()
    {
        var rendered = SpecificationRenderer.RenderReadiness(SampleReadiness());

        Assert.Contains("## Verdict", rendered);
        Assert.Contains("READY", rendered);
        Assert.Contains("## Conflicts", rendered);
        Assert.Contains("Pagination strategy is still open.", rendered);
        Assert.Contains("## Residuals", rendered);
        Assert.Contains("Legacy data import is out of scope.", rendered);
        Assert.Contains("## Slices", rendered);
        Assert.Contains("SL-001", rendered);
    }

    [Fact]
    public void RenderReadiness_ColecaoVazia_MantemSecaoComPlaceholder()
    {
        var readiness = SampleReadiness() with { Slices = [], Conflicts = [], Residuals = [] };

        var rendered = SpecificationRenderer.RenderReadiness(readiness);

        Assert.Contains("## Conflicts\n_None._", rendered);
        Assert.Contains("## Residuals\n_None._", rendered);
        Assert.Contains("## Slices\n_None._", rendered);
    }

    [Fact]
    public void RenderReadiness_EscapaCaracteresEspeciaisDeMarkdown()
    {
        var readiness = SampleReadiness() with
        {
            Conflicts = ["Uses *asterisks* and [brackets]."],
        };

        var rendered = SpecificationRenderer.RenderReadiness(readiness);

        Assert.Contains("\\*asterisks\\*", rendered);
        Assert.Contains("\\[brackets\\]", rendered);
    }
}
