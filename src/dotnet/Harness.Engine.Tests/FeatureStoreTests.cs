using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// feature_list.json is the "persistent artifact" that survives the development flow's
/// context hard resets: deterministic selection of the next pending feature and
/// termination when all pass. Same tolerance as the other stores — missing/unreadable →
/// empty list, never brings down the run.
/// </summary>
public class FeatureStoreTests : IDisposable
{
    public FeatureStoreTests() => FeatureStore.Reset();
    public void Dispose() => FeatureStore.Reset();

    [Fact]
    public void WriteELoad_FazemRoundtrip()
    {
        FeatureStore.Write([new Feature(1, "A", 2, false), new Feature(2, "B", 1, false)]);

        var loaded = FeatureStore.Load();

        Assert.Equal(2, loaded.Count);
        Assert.Equal("A", loaded[0].Title);
    }

    [Fact]
    public void Write_FormataJsonParaLeitura()
    {
        FeatureStore.Write([new Feature(1, "A", 2, false)]);

        var json = File.ReadAllText(".harness/feature_list.json");

        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("  \"items\": [", json);
        Assert.Contains("      \"title\": \"A\"", json);
    }

    [Fact]
    public void Parse_ArrayCru_ForcaPendenteEPreservaCampos()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]""");

        Assert.Equal(2, features.Count);
        Assert.All(features, f => Assert.False(f.Passes)); // every feature is born pending
        Assert.Equal("Login", features[0].Title);
    }

    [Fact]
    public void Parse_SemId_Reindexa()
    {
        var features = FeatureStore.Parse("""[{"title":"X","priority":1},{"title":"Y","priority":1}]""");

        Assert.Equal([1, 2], features.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Parse_IdExplicitoDuplicado_RetornaVazio()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"A","priority":1},{"id":1,"title":"B","priority":2}]""");

        Assert.Empty(features);
    }

    [Fact]
    public void Parse_TituloVazioOuPrioridadeNaoPositiva_RetornaVazio()
    {
        Assert.Empty(FeatureStore.Parse("""[{"id":1,"title":"","priority":1}]"""));
        Assert.Empty(FeatureStore.Parse("""[{"id":1,"title":"A","priority":0}]"""));
    }

    [Fact]
    public void Parse_IdAusenteNaoColideComIdExplicito()
    {
        var features = FeatureStore.Parse(
            """[{"title":"A","priority":1},{"id":1,"title":"B","priority":2}]""");

        Assert.Equal([2, 1], features.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Parse_JsonInvalido_RetornaVazioSemLancar()
    {
        Assert.Empty(FeatureStore.Parse("this is not json"));
        Assert.Empty(FeatureStore.Parse("[]"));
    }

    [Fact]
    public void NextPending_EscolheMaiorPrioridadePendente()
    {
        FeatureStore.Write([
            new Feature(1, "low", 3, false),
            new Feature(2, "high", 1, false),
            new Feature(3, "medium", 2, true), // already passing — ignored
        ]);

        Assert.Equal(2, FeatureStore.NextPending()!.Id); // priority 1
    }

    [Fact]
    public void Parse_DependsOnAusente_NormalizaParaArrayVazio()
    {
        var features = FeatureStore.Parse("""[{"id":1,"title":"X","priority":1}]""");

        Assert.Empty(features[0].Deps);
    }

    [Fact]
    public void Parse_DescriptionEReferencesAusentes_NormalizamParaVazio()
    {
        var features = FeatureStore.Parse("""[{"id":1,"title":"X","priority":1}]""");

        Assert.Equal("", features[0].Description);
        Assert.Empty(features[0].Refs);
        Assert.Equal("", features[0].ImplementationContext);
    }

    [Fact]
    public void Parse_PreservaDescriptionEReferences()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"X","priority":1,"description":"does Y","references":["RF-003"],"implementationContext":"inline Y"}]""");

        Assert.Equal("does Y", features[0].Description);
        Assert.Equal(["RF-003"], features[0].Refs);
        Assert.Equal("inline Y", features[0].ImplementationContext);
    }

    [Fact]
    public void Parse_DescriptionAcimaDoTeto_ETruncada()
    {
        var longDescription = new string('a', FeatureStore.DescriptionMaxChars + 50);

        var features = FeatureStore.Parse(
            $$"""[{"id":1,"title":"X","priority":1,"description":"{{longDescription}}"}]""");

        Assert.Equal(FeatureStore.DescriptionMaxChars, features[0].Description.Length);
    }

    [Fact]
    public void Parse_ImplementationContextAcimaDoTeto_ETruncado()
    {
        var longContext = new string('a', FeatureStore.ImplementationContextMaxChars + 50);

        var features = FeatureStore.Parse(
            $$"""[{"id":1,"title":"X","priority":1,"implementationContext":"{{longContext}}"}]""");

        Assert.Equal(FeatureStore.ImplementationContextMaxChars, features[0].ImplementationContext.Length);
    }

    [Fact]
    public void Parse_DependsOnCiclico_RetornaVazioSemLancar()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"A","priority":1,"dependsOn":[2]},{"id":2,"title":"B","priority":2,"dependsOn":[1]}]""");

        Assert.Empty(features);
    }

    [Fact]
    public void Parse_DependsOnAutoReferencia_RetornaVazio()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"A","priority":1,"dependsOn":[1]}]""");

        Assert.Empty(features);
    }

    [Fact]
    public void Parse_DependsOnIdInexistente_RetornaVazio()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"A","priority":1,"dependsOn":[99]}]""");

        Assert.Empty(features);
    }

    [Fact]
    public void Load_FeatureListLegadoSemDependsOn_NaoLanca()
    {
        // Simulates a feature_list.json written by an earlier harness version, without the
        // "dependsOn" key — proves the backward compatibility that motivated the Deps design.
        Directory.CreateDirectory(".harness");
        File.WriteAllText(".harness/feature_list.json",
            """{"items":[{"id":1,"title":"A","priority":1,"passes":false}]}""");

        var loaded = FeatureStore.Load();

        Assert.Single(loaded);
        Assert.Empty(loaded[0].Deps);
    }

    [Fact]
    public void NextPending_IgnoraFeatureComDependenciaPendente()
    {
        FeatureStore.Write([
            new Feature(1, "foundation", 2, false),
            new Feature(2, "depends on 1", 1, false, [1]), // "better" priority, but blocked
        ]);

        Assert.Equal(1, FeatureStore.NextPending()!.Id);
    }

    [Fact]
    public void NextPending_LiberaFeatureAposDependenciaPassar()
    {
        FeatureStore.Write([
            new Feature(1, "foundation", 2, false),
            new Feature(2, "depends on 1", 1, false, [1]),
        ]);
        Assert.Equal(1, FeatureStore.NextPending()!.Id);

        FeatureStore.MarkPassed(1);

        Assert.Equal(2, FeatureStore.NextPending()!.Id);
    }

    [Fact]
    public void NextPending_TodasBloqueadas_RetornaNullComPendenciasExistentes()
    {
        // Cyclic graph written directly via Write (bypassing Parse's validation) — simulates
        // a feature_list.json hand-edited outside the normal flow.
        FeatureStore.Write([
            new Feature(1, "A", 1, false, [2]),
            new Feature(2, "B", 2, false, [1]),
        ]);

        Assert.Null(FeatureStore.NextPending());
        Assert.Equal(2, FeatureStore.PendingCount());
    }

    [Fact]
    public void MarkPassed_ViraAFeatureE_AllPassing_FechaQuandoTodasPassam()
    {
        FeatureStore.Write([new Feature(1, "A", 1, false), new Feature(2, "B", 2, false)]);

        FeatureStore.MarkPassed(1);
        Assert.Equal(1, FeatureStore.PendingCount());
        Assert.False(FeatureStore.AllPassing());

        FeatureStore.MarkPassed(2);
        Assert.Equal(0, FeatureStore.PendingCount());
        Assert.True(FeatureStore.AllPassing());
        Assert.Null(FeatureStore.NextPending());
    }

    [Fact]
    public void AllPassing_ListaVazia_EhFalso()
    {
        Assert.False(FeatureStore.AllPassing()); // nothing written → not "all passing"
    }

    [Fact]
    public void Reset_ApagaALista()
    {
        FeatureStore.Write([new Feature(1, "A", 1, false)]);
        FeatureStore.Reset();

        Assert.Empty(FeatureStore.Load());
    }
}
