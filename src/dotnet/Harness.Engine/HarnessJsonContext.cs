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

/// <summary>
/// Contexto dedicado à visão persistida das features. Mantê-lo separado evita alterar a
/// serialização compacta dos demais stores e dos eventos JSONL.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(FeatureList))]
internal partial class PrettyFeatureListJsonContext : JsonSerializerContext;
