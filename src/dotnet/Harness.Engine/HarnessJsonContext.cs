using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// Compile-time-generated serialization metadata. Native AOT doesn't allow the reflection
/// JsonSerializer uses by default — the source generator resolves this and eliminates
/// the trimming warnings.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(ContextUsage))]
[JsonSerializable(typeof(HarnessState))]
[JsonSerializable(typeof(HarnessConfig))]
[JsonSerializable(typeof(ArtifactManifest))]
[JsonSerializable(typeof(TraceEntry))]
[JsonSerializable(typeof(ScoreReport))]
[JsonSerializable(typeof(GoldenCase))]
[JsonSerializable(typeof(Feature))]
[JsonSerializable(typeof(FeatureList))]
[JsonSerializable(typeof(RunConfig))]
// Raw array to deserialize what the driver returns in `plan` (`[{id,title,priority}, ...]`).
[JsonSerializable(typeof(Feature[]), TypeInfoPropertyName = "FeatureArray")]
internal partial class HarnessJsonContext : JsonSerializerContext;

/// <summary>
/// Context dedicated to the persisted view of features. Keeping it separate avoids
/// altering the compact serialization of the other stores and the JSONL events.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(FeatureList))]
internal partial class PrettyFeatureListJsonContext : JsonSerializerContext;
