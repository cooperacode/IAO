using System.Text.Json.Serialization;

namespace Flows.Specification;

/// <summary>
/// Compile-time-generated serialization metadata for the Specification domain model, mirroring
/// <c>Harness.Engine.HarnessJsonContext</c>. Native AOT doesn't allow the reflection
/// JsonSerializer uses by default — the source generator resolves this and eliminates
/// the trimming warnings.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IdeaFrame))]
[JsonSerializable(typeof(OpenQuestion))]
[JsonSerializable(typeof(PrdDocument))]
[JsonSerializable(typeof(Goal))]
[JsonSerializable(typeof(Metric))]
[JsonSerializable(typeof(Risk))]
[JsonSerializable(typeof(Decision))]
[JsonSerializable(typeof(SoftwareSpecification))]
[JsonSerializable(typeof(Requirement))]
[JsonSerializable(typeof(AcceptanceCriterion))]
[JsonSerializable(typeof(InterfaceContract))]
[JsonSerializable(typeof(DataRule))]
[JsonSerializable(typeof(DeliveryContract))]
[JsonSerializable(typeof(Adr))]
[JsonSerializable(typeof(InterfaceControl))]
[JsonSerializable(typeof(ReadinessSlice))]
[JsonSerializable(typeof(ReadinessVerdict))]
[JsonSerializable(typeof(ApprovalDecision))]
[JsonSerializable(typeof(RunState))]
internal partial class SpecificationJsonContext : JsonSerializerContext;
