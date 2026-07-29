using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// The store is what keeps the envelope minimal (token savings): the accumulated state
/// lives in a file between invocations, not in the context window.
/// </summary>
public class StateStoreTests : IDisposable
{
    public StateStoreTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void SetEGet_PersistemEntreChamadas()
    {
        StateStore.Set("description", "Login with Google");

        Assert.Equal("Login with Google", StateStore.Get("description"));
    }

    [Fact]
    public void Get_ChaveInexistente_RetornaNull()
    {
        Assert.Null(StateStore.Get("does-not-exist"));
    }

    [Fact]
    public void Set_SobrescreveAChaveExistente()
    {
        StateStore.Set("type", "Bug");
        StateStore.Set("type", "Epic");

        Assert.Equal("Epic", StateStore.Get("type"));
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
        StateStore.Set("description", "x");
        StateStore.Increment();

        Assert.Equal("x", StateStore.Get("description"));
    }

    [Fact]
    public void Reset_LimpaContadorEDados()
    {
        StateStore.Set("description", "x");
        StateStore.Increment();

        StateStore.Reset();

        Assert.Equal(0, StateStore.Load().Step);
        Assert.Null(StateStore.Get("description"));
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
