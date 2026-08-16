using System.Text.Json;
using Flows.Specification;
using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// End-to-end trace across the Specification/Development boundary (blueprint 0004 §9
/// Integração, blueprint 0006 "Prática: construa uma linha de rastreabilidade"): a TodoApp
/// accepted idea→PRD→SRS→SDD→readiness chain is published via
/// <see cref="SpecificationPublisher.Publish"/>, re-read byte-for-byte via the REAL
/// <see cref="DocsReader.Read"/> (the same function Development's brief-from-docs path uses),
/// then a test-side mapping — standing in for what a driver following <c>dev-initializer</c>
/// would produce from the bundle — turns each accepted <see cref="ReadinessSlice"/> into a raw
/// feature-array JSON entry and feeds it through the REAL <see cref="FeatureStore.Parse"/>.
/// Every assertion below re-uses the real <see cref="FeatureStore"/>/<see cref="Flows.Development.DevelopmentTasks"/>
/// constants (<see cref="FeatureStore.DescriptionMaxChars"/>, <see cref="FeatureStore.ImplementationContextMaxChars"/>,
/// <see cref="Flows.Development.DevelopmentTasks.MaxFeatures"/>) instead of restating 700/4000/10
/// as literals.
/// </summary>
public class SpecificationToDevelopmentTests : IDisposable
{
    // "specs" folder relative to the test process's CWD — mirrors SpecificationPublisherTests'
    // convention; only these tests populate it, created/deleted per test.
    private static readonly string SpecsDir = Path.Combine(Directory.GetCurrentDirectory(), "specs");
    private static readonly string ActiveDir = Path.Combine(SpecsDir, "active");

    private const string PrdFilename = "00-prd.md";
    private const string SrsFilename = "10-software-requirements-specification.md";
    private const string SddFilename = "20-software-design-document.md";
    private const string ReadinessFilename = "30-readiness-handoff.md";

    public SpecificationToDevelopmentTests() => Clean();

    public void Dispose() => Clean();

    private static void Clean()
    {
        SpecificationStore.Reset(); // also removes publish-manifest.json (same directory)
        if (Directory.Exists(SpecsDir))
            Directory.Delete(SpecsDir, recursive: true);
    }

    // --- TodoApp fixture (blueprint 0006's traceability exercise, expressed in this
    // project's real Specification models — not the unrelated legacy Flows.Refinement
    // TodoApp markdown docs under assets/use-cases/) --------------------------------------

    private static PrdDocument TodoPrd() => new(
        "iao/prd/v1", "sha256:idea", "Let a single user manage a personal todo list",
        [
            new Goal("OBJ-1", "Users can create todo items"),
            new Goal("OBJ-2", "Users can complete todo items"),
            new Goal("OBJ-3", "Users can delete todo items"),
        ],
        [
            new Metric("MET-1", "OBJ-1", "items created per session", "at least 1"),
            new Metric("MET-2", "OBJ-2", "items completed", "tracked"),
            new Metric("MET-3", "OBJ-3", "items deleted", "tracked"),
        ],
        ["multi-user collaboration"], ["todo item CRUD"], [], [], []);

    private static SoftwareSpecification TodoSrs() => new(
        "iao/srs/v1", "sha256:prd",
        [
            new Requirement("RF-1", ["OBJ-1"], "Create a todo item", [], ["AC-1"]),
            new Requirement("RF-2", ["OBJ-2"], "Complete a todo item", ["RF-1"], ["AC-2"]),
            new Requirement("RF-3", ["OBJ-3"], "Delete a todo item", ["RF-1"], ["AC-3"]),
        ],
        [],
        [
            new AcceptanceCriterion("AC-1", ["RF-1"], "no items exist", "a user adds an item", "it appears in the list"),
            new AcceptanceCriterion("AC-2", ["RF-2"], "an item exists", "a user marks it done", "it is shown as completed"),
            new AcceptanceCriterion("AC-3", ["RF-3"], "an item exists", "a user deletes it", "it no longer appears in the list"),
        ],
        [], [],
        new DeliveryContract("webapi", "integration tests", true));

    private static SoftwareDesignDocument TodoSdd() => new(
        "iao/sdd/v1", "sha256:srs",
        [
            new Adr("ADR-1", "In-memory store", "Use an in-memory repository for todo items", "Simplicity for a bootstrap target", ["RF-1"]),
            new Adr("ADR-2", "REST completion endpoint", "Expose PATCH /todos/{id}/complete", "Idempotent completion", ["RF-2"]),
            new Adr("ADR-3", "REST delete endpoint", "Expose DELETE /todos/{id}", "Standard REST semantics", ["RF-3"]),
        ],
        []);

    private static ReadinessVerdict TodoReadiness() => new(
        "READY",
        [
            new ReadinessSlice("SL-1", "core", "Create a todo item", ["item creation"], [], "a created item appears in the list",
                ["RF-1"], ["ADR-1"], [], ["POST /todos"], "user submits a title", "empty title is rejected",
                "the item appears in the response", "webapi", "integration test"),
            new ReadinessSlice("SL-2", "core", "Complete a todo item", ["item completion"], [], "a completed item is marked done",
                ["RF-2"], ["ADR-2"], ["SL-1"], ["PATCH /todos/{id}/complete"], "user marks an item done", "unknown id is rejected",
                "the item is shown as completed", "webapi", "integration test"),
            new ReadinessSlice("SL-3", "core", "Delete a todo item", ["item deletion"], [], "a deleted item no longer appears",
                ["RF-3"], ["ADR-3"], ["SL-1"], ["DELETE /todos/{id}"], "user deletes an item", "unknown id is rejected",
                "the item no longer appears in the list", "webapi", "integration test"),
        ],
        [], []);

    // --- test-side planner mapping: ReadinessSlice -> raw feature-array JSON entry --------
    // Stands in for what a driver following dev-initializer would produce after reading the
    // published bundle. Not production code — it is scaffolding for this test, mirroring the
    // FeaturesShape contract in Flows.Development/DevelopmentTasks.Prompt.cs
    // ({"id":..,"title":..,"priority":..,"dependsOn":[..],"description":"..","references":[..],
    // "implementationContext":{"requirements":[..],"constraints":[..],"files":[..],"acceptance":[..]}}).

    private static string BuildFeaturePlanJson(SoftwareSpecification srs, ReadinessVerdict readiness)
    {
        var requirementsById = srs.FunctionalRequirements
            .Concat(srs.QualityRequirements)
            .ToDictionary(r => r.Id);

        var sliceIdToFeatureId = readiness.Slices
            .Select((slice, index) => (slice.Id, FeatureId: index + 1))
            .ToDictionary(x => x.Id, x => x.FeatureId);

        var features = readiness.Slices.Select((slice, index) =>
        {
            var objIds = slice.RequirementIds
                .SelectMany(rf => requirementsById.TryGetValue(rf, out var requirement) ? requirement.GoalIds : [])
                .Distinct();
            var acIds = slice.RequirementIds
                .SelectMany(rf => requirementsById.TryGetValue(rf, out var requirement) ? requirement.AcceptanceIds : [])
                .Distinct();

            var references = slice.RequirementIds
                .Concat(slice.AdrIds)
                .Concat(objIds)
                .Concat(acIds)
                .Distinct()
                .ToArray();

            return new
            {
                id = sliceIdToFeatureId[slice.Id],
                title = slice.Goal,
                priority = index + 1,
                dependsOn = slice.DependsOn.Select(d => sliceIdToFeatureId[d]).ToArray(),
                description = $"{slice.Goal}. Observable outcome: {slice.ObservableOutcome}. "
                    + $"Happy path: {slice.HappyPath}. Failure path: {slice.FailurePath}.",
                references,
                implementationContext = new
                {
                    requirements = slice.RequirementIds,
                    constraints = slice.Contracts,
                    files = new[] { slice.SuggestedTarget },
                    acceptance = new[] { slice.AcceptanceCriterion },
                },
            };
        });

        return JsonSerializer.Serialize(features);
    }

    // --- publish -> DocsReader ------------------------------------------------------------

    [Fact]
    public void PublishedTodoBundle_DocsReaderRead_DevolveOsQuatroArquivosComOConteudoPublicadoExato()
    {
        var result = SpecificationPublisher.Publish(TodoPrd(), TodoSrs(), TodoSdd(), TodoReadiness());
        Assert.True(result.Success, result.Error);

        var (content, files) = DocsReader.Read("specs/active");

        Assert.Equal([PrdFilename, SrsFilename, SddFilename, ReadinessFilename], files);
        // DocsReader.Read trims trailing whitespace off the WHOLE concatenated result (only
        // once, at the very end) — so for every file except the last, the on-disk text is a
        // verbatim substring of content; for the last file (readiness), its own trailing
        // newline(s) are among what gets trimmed, so compare with TrimEnd() on both sides,
        // matching Read's own trimming instead of asserting on whitespace it deliberately drops.
        Assert.Contains(File.ReadAllText(Path.Combine(ActiveDir, PrdFilename)).TrimEnd(), content);
        Assert.Contains(File.ReadAllText(Path.Combine(ActiveDir, SrsFilename)).TrimEnd(), content);
        Assert.Contains(File.ReadAllText(Path.Combine(ActiveDir, SddFilename)).TrimEnd(), content);
        Assert.Contains(File.ReadAllText(Path.Combine(ActiveDir, ReadinessFilename)).TrimEnd(), content);
        var planPath = Path.Combine(ActiveDir, SpecificationPublisher.DevelopmentPlanFilename);
        Assert.True(File.Exists(planPath));
        var imported = FeatureStore.ParseDevelopmentPlan(File.ReadAllText(planPath));
        Assert.Equal(3, imported.Count);
        Assert.Equal("Create a todo item", imported[0].Title);
        Assert.Contains(
            "ADR-1: In-memory store. Decision: Use an in-memory repository for todo items. Rationale: Simplicity for a bootstrap target",
            imported[0].Context.DecisionItems);
    }

    // --- bundle -> planner brief -> FeatureStore.Parse -------------------------------------

    [Fact]
    public void ReadinessSlices_MapeadasParaFeaturePlan_PreservamReferenciasEAcceptanceDentroDosLimites()
    {
        var srs = TodoSrs();
        var readiness = TodoReadiness();

        var json = BuildFeaturePlanJson(srs, readiness);
        var features = FeatureStore.Parse(json);

        Assert.Equal(readiness.Slices.Length, features.Count);
        Assert.True(features.Count <= Flows.Development.DevelopmentTasks.MaxFeatures);

        foreach (var (slice, feature) in readiness.Slices.Zip(features))
        {
            // OBJ-*/RF-*/AC-* references from the source slice all survive the mapping.
            foreach (var requirementId in slice.RequirementIds)
                Assert.Contains(requirementId, feature.Refs);
            foreach (var adrId in slice.AdrIds)
                Assert.Contains(adrId, feature.Refs);

            Assert.True(feature.Description.Length <= FeatureStore.DescriptionMaxChars);

            var totalContextChars = feature.Context.RequirementItems.Sum(s => s.Length)
                + feature.Context.DecisionItems.Sum(s => s.Length)
                + feature.Context.ConstraintItems.Sum(s => s.Length)
                + feature.Context.FileItems.Sum(s => s.Length)
                + feature.Context.AcceptanceItems.Sum(s => s.Length);
            Assert.True(totalContextChars <= FeatureStore.ImplementationContextMaxChars);

            // Blueprint 0004 §7: at least one objective acceptance criterion survives
            // persistence — proving this small, well-within-budget plan didn't lose it to
            // truncation.
            Assert.NotEmpty(feature.Context.AcceptanceItems);
            Assert.Contains(slice.AcceptanceCriterion, feature.Context.AcceptanceItems);
        }
    }

    [Fact]
    public void ReadinessSlices_MapeadasParaFeaturePlan_DependsOnAcompanhaOGrafoDasFatias()
    {
        var srs = TodoSrs();
        var readiness = TodoReadiness();

        var features = FeatureStore.Parse(BuildFeaturePlanJson(srs, readiness));

        // SL-1 has no dependency (blueprint 0006's "starting slice"); SL-2/SL-3 depend on it.
        var byTitle = features.ToDictionary(f => f.Title);
        Assert.Empty(byTitle["Create a todo item"].Deps);
        Assert.Equal([byTitle["Create a todo item"].Id], byTitle["Complete a todo item"].Deps);
        Assert.Equal([byTitle["Create a todo item"].Id], byTitle["Delete a todo item"].Deps);
    }

    // --- deliberately oversized context: prove the real truncation order -------------------

    [Fact]
    public void ImplementationContextEstourado_TruncaNaOrdemRequirementsConstraintsFilesAcceptance_EPerdeAcceptancePrimeiro()
    {
        // A single requirements entry that alone fills the whole ImplementationContext budget,
        // matching FeatureStore.TruncateImplementationContext's real fill order: requirements
        // first, then constraints, then files, then acceptance last — so when the budget is
        // exhausted by requirements alone, acceptance is the first (and here, only) casualty.
        var hugeRequirement = new string('r', FeatureStore.ImplementationContextMaxChars);

        var feature = new
        {
            id = 1,
            title = "Oversized slice",
            priority = 1,
            dependsOn = Array.Empty<int>(),
            description = "deliberately oversized implementation context",
            references = new[] { "RF-1", "ADR-1" },
            implementationContext = new
            {
                requirements = new[] { hugeRequirement },
                constraints = new[] { "a constraint that will not fit either" },
                files = new[] { "src/TodoApp/TodoController.cs" },
                acceptance = new[] { "the acceptance criterion text" },
            },
        };

        var features = FeatureStore.Parse(JsonSerializer.Serialize(new[] { feature }));

        Assert.Single(features);
        var parsed = features[0];

        Assert.Equal(FeatureStore.ImplementationContextMaxChars, parsed.Context.RequirementItems.Sum(s => s.Length));
        Assert.Empty(parsed.Context.ConstraintItems);
        Assert.Empty(parsed.Context.FileItems);
        Assert.Empty(parsed.Context.AcceptanceItems); // acceptance is the first thing lost once requirements alone exhaust the budget
    }
}
