namespace Flows.Specification;

// Authoritative domain model for the Specification flow (idea → PRD → SRS → SDD → readiness
// → approval → publish). Source: specs/0004-blueprint-prd-dotnet.html §3 "Modelo autoritativo".
//
// This file only declares shapes — no I/O, evaluators, or state machine behavior. Stores,
// evaluators and the state machine are later slices; keeping the model isolated lets those
// slices depend on stable records instead of re-deriving field names.
//
// `sealed record` with positional parameters, matching the exact snippet in the blueprint
// (IdeaFrame, PrdDocument, SoftwareSpecification, Requirement, AcceptanceCriterion are copied
// verbatim from §3). The remaining types aren't spelled out in the blueprint's code block, but
// are inferred from the evaluator predicates in §5 and the readiness contract in §7 — see the
// per-record notes below for the source of each field.

/// <summary>
/// A single open question raised during discovery or product framing.
/// <see cref="Blocking"/> backs the PrdEvaluator predicate "zero bloqueador aberto" (§5): a
/// PRD can't be accepted while any of its open questions is both unanswered and blocking.
/// </summary>
public sealed record OpenQuestion(
    string Id,
    string Question,
    bool Blocking);

/// <summary>Idea framing produced by the <c>discover</c> phase (§2, §3).</summary>
public sealed record IdeaFrame(
    string Schema,
    string Title,
    string Problem,
    string[] Users,
    string[] DesiredOutcomes,
    string[] Constraints,
    OpenQuestion[] OpenQuestions);

/// <summary>
/// A PRD goal. <see cref="Id"/> is the join key <see cref="Metric.GoalId"/> and
/// <see cref="Requirement.GoalIds"/> use to trace RF/RNF/SEC back to product intent (§7:
/// "matriz OBJ → RF/RNF/SEC → componente/ADR → fatia").
/// </summary>
public sealed record Goal(
    string Id,
    string Statement);

/// <summary>
/// A success metric tied to a <see cref="Goal"/>. <see cref="Measure"/> and
/// <see cref="Target"/> are kept as separate fields per the PrdEvaluator predicate
/// "medida/alvo separados" (§5) — a metric that conflates the two can't be validated
/// mechanically.
/// </summary>
public sealed record Metric(
    string Id,
    string GoalId,
    string Measure,
    string Target);

/// <summary>A named risk carried by the PRD, with its mitigation and severity.</summary>
public sealed record Risk(
    string Id,
    string Description,
    string Mitigation,
    string Severity);

/// <summary>A product decision recorded with its rationale, so it isn't silently reopened.</summary>
public sealed record Decision(
    string Id,
    string Statement,
    string Rationale);

/// <summary>PRD produced by the <c>product</c> phase (§2, §3).</summary>
public sealed record PrdDocument(
    string Schema,
    string IdeaDigest,
    string Vision,
    Goal[] Goals,
    Metric[] SuccessMetrics,
    string[] NonGoals,
    string[] Scope,
    Risk[] Risks,
    Decision[] Decisions,
    OpenQuestion[] OpenQuestions);

/// <summary>
/// A functional or quality requirement (RF/RNF/SEC). <see cref="DependsOn"/> and
/// <see cref="AcceptanceIds"/> are the edges the SrsEvaluator walks to reject dangling
/// references and orphan requirements (§5: "sem referência órfã").
/// </summary>
public sealed record Requirement(
    string Id,
    string[] GoalIds,
    string Statement,
    string[] DependsOn,
    string[] AcceptanceIds);

/// <summary>Given/When/Then acceptance criterion, linked back to the requirements it verifies.</summary>
public sealed record AcceptanceCriterion(
    string Id,
    string[] RequirementIds,
    string Given,
    string When,
    string Then);

/// <summary>
/// An interface the specification commits to, linked to the requirements it satisfies —
/// the SddEvaluator predicate "interfaces ... ligados a requisitos existentes" (§5) needs
/// <see cref="RequirementIds"/> to reject links to requirements that don't exist.
/// </summary>
public sealed record InterfaceContract(
    string Id,
    string[] RequirementIds,
    string Name,
    string Description);

/// <summary>A data invariant or constraint, linked to the requirements that motivate it.</summary>
public sealed record DataRule(
    string Id,
    string[] RequirementIds,
    string Rule);

/// <summary>
/// Delivery expectations handed to Development. <see cref="IsBootstrap"/> reflects §7: the
/// suggested target/verification strategy is labeled as a bootstrap contract when the
/// slice is greenfield, rather than treated as an established one.
/// </summary>
public sealed record DeliveryContract(
    string Target,
    string VerificationStrategy,
    bool IsBootstrap);

/// <summary>SRS produced by the <c>analysis</c> phase; SDD's per-requirement contracts (§2, §3).</summary>
public sealed record SoftwareSpecification(
    string Schema,
    string PrdDigest,
    Requirement[] FunctionalRequirements,
    Requirement[] QualityRequirements,
    AcceptanceCriterion[] AcceptanceCriteria,
    InterfaceContract[] Interfaces,
    DataRule[] DataRules,
    DeliveryContract Delivery);

/// <summary>
/// Architecture Decision Record produced by the <c>design</c> phase. <see cref="RequirementIds"/>
/// is the RF/RNF/SEC allocation the SddEvaluator checks for uniqueness and coverage (§5:
/// "ADRs únicos; alocação RF/RNF/SEC").
/// </summary>
public sealed record Adr(
    string Id,
    string Title,
    string Decision,
    string Rationale,
    string[] RequirementIds);

/// <summary>
/// A design-time interface or security control, linked to the requirements it implements —
/// the SDD counterpart to <see cref="InterfaceContract"/> (§5: "interfaces e controles
/// ligados a requisitos existentes").
/// </summary>
public sealed record InterfaceControl(
    string Id,
    string Name,
    string Description,
    string[] RequirementIds);

/// <summary>
/// SDD produced by the <c>design</c> phase — the top-level document wrapping <see cref="Adr"/>
/// and <see cref="InterfaceControl"/> items, mirroring how <see cref="SoftwareSpecification"/>
/// wraps <see cref="Requirement"/>/<see cref="AcceptanceCriterion"/> for the SRS (§2, §3).
/// <see cref="SrsDigest"/> is the parent-digest freshness field SddEvaluator checks (§5:
/// "prdDigest atual" pattern, one phase down).
/// </summary>
public sealed record SoftwareDesignDocument(
    string Schema,
    string SrsDigest,
    Adr[] Adrs,
    InterfaceControl[] Controls);

/// <summary>
/// One readiness slice: the unit the Development planner turns into a feature. Fields mirror
/// the readiness contract in §7 verbatim — classification, scope, the OBJ → RF/RNF/SEC →
/// ADR → slice matrix, real dependencies (capped at ten slices per run), contracts/data/error
/// notes, happy/failure path, a binary acceptance criterion, and the suggested target/strategy.
/// </summary>
public sealed record ReadinessSlice(
    string Id,
    string Classification,
    string Goal,
    string[] InScope,
    string[] OutOfScope,
    string ObservableOutcome,
    string[] RequirementIds,
    string[] AdrIds,
    string[] DependsOn,
    string[] Contracts,
    string HappyPath,
    string FailurePath,
    string AcceptanceCriterion,
    string SuggestedTarget,
    string SuggestedVerificationStrategy);

/// <summary>
/// Readiness output produced by the <c>review</c> phase. <see cref="Conflicts"/> and
/// <see cref="Residuals"/> back the ReadinessEvaluator predicate "conflitos/resíduos
/// explícitos" (§5) — unresolved conflicts or residual risk must be declared, not implied.
/// </summary>
public sealed record ReadinessVerdict(
    string Verdict,
    ReadinessSlice[] Slices,
    string[] Conflicts,
    string[] Residuals);

/// <summary>
/// The human approval decision recorded for a bundle. <see cref="BundleDigest"/> backs the
/// ApprovalEvaluator predicate "bundleDigest atual" (§5) — approval is bound to the exact
/// bundle previewed, not to whatever the accepted documents happen to say later.
/// </summary>
public sealed record ApprovalDecision(
    string Decision,
    string BundleDigest,
    string Rationale,
    string ApprovedBy,
    DateTimeOffset DecidedAt);

/// <summary>
/// Run state persisted across invocations, mirroring <c>Harness.Engine.HarnessState</c> for
/// this flow's own state machine (§2 status values; §4 StateStore bullet: "step, status,
/// phase, counters, trace label e razão terminal"). Top-level type (not nested) so it stays
/// servable by the AOT source generator.
/// </summary>
public sealed record RunState(
    int Step,
    string Status,
    string Phase,
    Dictionary<string, int> Counters,
    string? TraceLabel,
    string? TerminalReason);
