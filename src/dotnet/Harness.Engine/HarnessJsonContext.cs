using System.Text.Json.Serialization;

namespace Harness.Engine;

/// <summary>
/// Metadados de serialização gerados em tempo de compilação. Native AOT não permite a
/// reflexão que o JsonSerializer usa por padrão — o source generator resolve isso e
/// elimina os warnings de trimming.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Envelope))]
[JsonSerializable(typeof(HarnessState))]
[JsonSerializable(typeof(HarnessConfig))]
[JsonSerializable(typeof(ArtifactManifest))]
[JsonSerializable(typeof(TraceEntry))]
[JsonSerializable(typeof(ScoreReport))]
[JsonSerializable(typeof(GoldenCase))]
[JsonSerializable(typeof(Feature))]
[JsonSerializable(typeof(FeatureList))]
[JsonSerializable(typeof(RunConfig))]
// Array cru p/ desserializar o que o driver devolve no `plan` (`[{id,title,priority}, ...]`).
[JsonSerializable(typeof(Feature[]), TypeInfoPropertyName = "FeatureArray")]
internal partial class HarnessJsonContext : JsonSerializerContext;
