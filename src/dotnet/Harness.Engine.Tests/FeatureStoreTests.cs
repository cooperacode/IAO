using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// A feature_list.json é o "persistent artifact" que atravessa os hard resets de contexto do
/// flow de desenvolvimento: seleção determinística da próxima pendente e término quando todas
/// passam. Mesma tolerância dos demais stores — ausente/ilegível → lista vazia, nunca derruba.
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
    public void Parse_ArrayCru_ForcaPendenteEPreservaCampos()
    {
        var features = FeatureStore.Parse(
            """[{"id":1,"title":"Login","priority":1},{"id":2,"title":"Logout","priority":3}]""");

        Assert.Equal(2, features.Count);
        Assert.All(features, f => Assert.False(f.Passes)); // toda feature nasce pendente
        Assert.Equal("Login", features[0].Title);
    }

    [Fact]
    public void Parse_SemId_Reindexa()
    {
        var features = FeatureStore.Parse("""[{"title":"X","priority":1},{"title":"Y","priority":1}]""");

        Assert.Equal([1, 2], features.Select(f => f.Id).ToArray());
    }

    [Fact]
    public void Parse_JsonInvalido_RetornaVazioSemLancar()
    {
        Assert.Empty(FeatureStore.Parse("isso não é json"));
        Assert.Empty(FeatureStore.Parse("[]"));
    }

    [Fact]
    public void NextPending_EscolheMaiorPrioridadePendente()
    {
        FeatureStore.Write([
            new Feature(1, "baixa", 3, false),
            new Feature(2, "alta", 1, false),
            new Feature(3, "media", 2, true), // já passa — ignorada
        ]);

        Assert.Equal(2, FeatureStore.NextPending()!.Id); // prioridade 1
    }

    [Fact]
    public void Parse_DependsOnAusente_NormalizaParaArrayVazio()
    {
        var features = FeatureStore.Parse("""[{"id":1,"title":"X","priority":1}]""");

        Assert.Empty(features[0].Deps);
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
        // Simula um feature_list.json gravado por uma versão anterior do harness, sem a chave
        // "dependsOn" — prova a compatibilidade retroativa que motivou o design com Deps.
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
            new Feature(1, "fundação", 2, false),
            new Feature(2, "depende de 1", 1, false, [1]), // prioridade "melhor", mas bloqueada
        ]);

        Assert.Equal(1, FeatureStore.NextPending()!.Id);
    }

    [Fact]
    public void NextPending_LiberaFeatureAposDependenciaPassar()
    {
        FeatureStore.Write([
            new Feature(1, "fundação", 2, false),
            new Feature(2, "depende de 1", 1, false, [1]),
        ]);
        Assert.Equal(1, FeatureStore.NextPending()!.Id);

        FeatureStore.MarkPassed(1);

        Assert.Equal(2, FeatureStore.NextPending()!.Id);
    }

    [Fact]
    public void NextPending_TodasBloqueadas_RetornaNullComPendenciasExistentes()
    {
        // Grafo cíclico gravado direto via Write (bypassando a validação de Parse) — simula um
        // feature_list.json editado à mão fora do fluxo normal.
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
        Assert.False(FeatureStore.AllPassing()); // nada gravado → não é "tudo passando"
    }

    [Fact]
    public void Reset_ApagaALista()
    {
        FeatureStore.Write([new Feature(1, "A", 1, false)]);
        FeatureStore.Reset();

        Assert.Empty(FeatureStore.Load());
    }
}
