using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O store é o que permite manter o envelope mínimo (economia de tokens): o estado
/// acumulado fica em arquivo entre invocações, não na janela de contexto.
/// </summary>
public class StateStoreTests : IDisposable
{
    public StateStoreTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void SetEGet_PersistemEntreChamadas()
    {
        StateStore.Set("descricao", "Login com Google");

        Assert.Equal("Login com Google", StateStore.Get("descricao"));
    }

    [Fact]
    public void Get_ChaveInexistente_RetornaNull()
    {
        Assert.Null(StateStore.Get("nao-existe"));
    }

    [Fact]
    public void Set_SobrescreveAChaveExistente()
    {
        StateStore.Set("tipo", "Bug");
        StateStore.Set("tipo", "Épico");

        Assert.Equal("Épico", StateStore.Get("tipo"));
    }

    [Fact]
    public void Increment_AvancaOContador()
    {
        Assert.Equal(1, StateStore.Increment());
        Assert.Equal(2, StateStore.Increment());
        Assert.Equal(3, StateStore.Increment());
        Assert.Equal(3, StateStore.Load().Step);
    }

    [Fact]
    public void Increment_PreservaOsDadosAcumulados()
    {
        StateStore.Set("descricao", "x");
        StateStore.Increment();

        Assert.Equal("x", StateStore.Get("descricao"));
    }

    [Fact]
    public void Reset_LimpaContadorEDados()
    {
        StateStore.Set("descricao", "x");
        StateStore.Increment();

        StateStore.Reset();

        Assert.Equal(0, StateStore.Load().Step);
        Assert.Null(StateStore.Get("descricao"));
    }

    [Fact]
    public void SetContextEGetContext_PersistemEntreChamadas()
    {
        StateStore.SetContext(new Dictionary<string, string> { ["driver"] = "claude code" });

        Assert.Equal("claude code", StateStore.GetContext()?["driver"]);
    }

    [Fact]
    public void GetContext_SemContextoDefinido_RetornaNull()
    {
        Assert.Null(StateStore.GetContext());
    }

    [Fact]
    public void Reset_LimpaOContexto()
    {
        StateStore.SetContext(new Dictionary<string, string> { ["driver"] = "claude code" });

        StateStore.Reset();

        Assert.Null(StateStore.GetContext());
    }
}
