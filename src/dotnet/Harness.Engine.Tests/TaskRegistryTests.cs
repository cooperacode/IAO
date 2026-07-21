using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Regressões do endurecimento: erro NUNCA pode virar "stop" silencioso, e o teto de
/// passos precisa cortar loop infinito (guarda de token).
/// </summary>
public class TaskRegistryTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
        ["finalize"] = _ => "stop",
    };

    public TaskRegistryTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void Dispatch_ComandoRegistrado_ExecutaAAction()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.Equal("PROMPT_START", result);
    }

    [Fact]
    public void Dispatch_RepassaArgsParaAAction()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["Login"]}"""], Tasks);

        Assert.Equal("PROMPT_CLASSIFY:Login", result);
    }

    [Fact]
    public void Dispatch_Finalize_RetornaStop()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"command","value":"finalize"}"""], Tasks);

        Assert.Equal("stop", result);
    }

    [Fact]
    public void Dispatch_ComandoInexistente_RetornaErroEnaoStop()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"tipo"}"""], Tasks);

        Assert.StartsWith("ERRO", result);
        Assert.NotEqual("stop", result);
        Assert.Contains("'tipo'", result);
    }

    [Fact]
    public void Dispatch_JsonMalformado_RetornaErroEnaoStop()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"""], Tasks);

        Assert.StartsWith("ERRO", result);
        Assert.NotEqual("stop", result);
    }

    [Fact]
    public void Dispatch_SemArgumento_RetornaErroEnaoStop()
    {
        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.StartsWith("ERRO", result);
        Assert.NotEqual("stop", result);
    }

    [Fact]
    public void Dispatch_MensagemDeErro_ListaOsComandosValidos()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"inexistente"}"""], Tasks);

        Assert.Contains("start", result);
        Assert.Contains("classify", result);
        Assert.Contains("finalize", result);
    }

    [Fact]
    public void Dispatch_Start_ReiniciaOContadorDePassos()
    {
        for (var i = 0; i < 5; i++)
            TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        Assert.Equal(5, StateStore.Load().Step);

        TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        // start reseta e então conta a si mesmo como passo 1.
        Assert.Equal(1, StateStore.Load().Step);
    }

    [Fact]
    public void Dispatch_Start_ComContext_PersisteNoStateStore()
    {
        TaskRegistry.Dispatch(
            ["""{"type":"text","value":"start","context":{"driver":"claude code"}}"""], Tasks);

        Assert.Equal("claude code", StateStore.GetContext()?["driver"]);
    }

    [Fact]
    public void Dispatch_ContextoSobreviveAoStart_EEReinjetadoViaPromptFormatter()
    {
        var tasksComPrompt = new Dictionary<string, Func<Envelope?, string>>
        {
            ["start"] = _ => PromptFormatter.Format(
                "instrução", new Envelope(EnvelopeType.Command, "plan", [])),
        };

        var result = TaskRegistry.Dispatch(
            ["""{"type":"text","value":"start","context":{"driver":"claude code"}}"""], tasksComPrompt);

        Assert.Contains("\"context\":{\"driver\":\"claude code\"}", result);
    }

    [Fact]
    public void Dispatch_AoExcederOTeto_ForcaStop()
    {
        // Consome exatamente o teto — todas essas ainda executam normalmente.
        for (var i = 0; i < TaskRegistry.MaxSteps; i++)
        {
            var ok = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
            Assert.NotEqual("stop", ok);
        }

        // O passo seguinte ultrapassa o teto e é cortado.
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        Assert.Equal("stop", result);
    }
}
