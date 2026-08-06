using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Hardening regressions: an error must NEVER turn into a silent "stop", and the step
/// ceiling has to cut off an infinite loop (token guard).
/// </summary>
public class TaskRegistryTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
        ["finalize"] = _ => "stop",
    };

    public TaskRegistryTests()
    {
        StateStore.Reset();
        Trace.Reset();
        HarnessLog.Reset();
    }

    public void Dispose()
    {
        StateStore.Reset();
        Trace.Reset();
        HarnessLog.Reset();
    }

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

        Assert.StartsWith("HARNESS PROTOCOL ERROR", result);
        Assert.NotEqual("stop", result);
        Assert.Contains("'tipo'", result);
    }

    [Fact]
    public void Dispatch_JsonMalformado_RetornaErroEnaoStop()
    {
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"""], Tasks);

        Assert.StartsWith("HARNESS PROTOCOL ERROR", result);
        Assert.NotEqual("stop", result);
    }

    [Fact]
    public void Dispatch_SemArgumento_RetornaErroEnaoStop()
    {
        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.StartsWith("HARNESS PROTOCOL ERROR", result);
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

        // start resets and then counts itself as step 1.
        Assert.Equal(1, StateStore.Load().Step);
    }

    [Fact]
    public void Dispatch_Start_ComShouldResetOnStartFalso_NaoTruncaStateNemTrace()
    {
        // "start" also arrives on a RESUME (a fresh session reopening a run in progress) —
        // the flow signals this via shouldResetOnStart, and Dispatch must not truncate anything.
        for (var i = 0; i < 3; i++)
            TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Trace.Append(99, "handoff", TraceOutcome.Instruction, 5);

        TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks, shouldResetOnStart: () => false);

        Assert.Equal(4, StateStore.Load().Step); // 3 previous + "start" itself, no reset
        Assert.Contains(Trace.Load(), e => e is { Step: 99, Command: "handoff" });
    }

    [Fact]
    public void Dispatch_Start_SemPredicado_MantemComportamentoPadraoDeSempreResetar()
    {
        for (var i = 0; i < 3; i++)
            TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.Equal(1, StateStore.Load().Step); // backward compatible: no predicate, always resets
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
        var tasksWithPrompt = new Dictionary<string, Func<Envelope?, string>>
        {
            ["start"] = _ => PromptFormatter.Format(
                "instruction", new Envelope(EnvelopeType.Command, "plan", [])),
        };

        var result = TaskRegistry.Dispatch(
            ["""{"type":"text","value":"start","context":{"driver":"claude code"}}"""], tasksWithPrompt);

        Assert.Contains("\"context\":{\"driver\":\"claude code\"}", result);
    }

    [Fact]
    public void Dispatch_AoExcederOTeto_ForcaStop()
    {
        // Consumes exactly the ceiling — all of these still run normally.
        for (var i = 0; i < TaskRegistry.MaxSteps; i++)
        {
            var ok = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
            Assert.NotEqual("stop", ok);
        }

        // The next step goes over the ceiling and gets cut off.
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        Assert.Equal("stop", result);
    }

    private static readonly Dictionary<string, Func<Envelope?, string>> FaultyTasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["boom"] = _ => throw new InvalidOperationException("a real bug, not a driver protocol error"),
    };

    [Fact]
    public void Dispatch_ActionLancaExcecaoNaoTratada_RetornaStopEnaoDeixaVazar()
    {
        // The regression this guards: before the "fault" guard, any exception a task action
        // threw (not just HarnessTimeoutException) propagated all the way out of Dispatch,
        // crashing the process instead of a graceful "stop" — see TaskRegistry.Resolve.
        var result = TaskRegistry.Dispatch(["""{"type":"tool","value":"boom"}"""], FaultyTasks);

        Assert.Equal("stop", result);
    }

    [Fact]
    public void Dispatch_ActionLancaExcecaoNaoTratada_GravaDesfechoFaultEMarcaTerminal()
    {
        TaskRegistry.Dispatch(["""{"type":"tool","value":"boom"}"""], FaultyTasks);

        Assert.Equal(TraceOutcome.Fault, Trace.Load()[^1].Outcome);
        Assert.Equal("fault", StateStore.TerminalReason());
    }

    [Fact]
    public void Dispatch_ActionLancaExcecaoNaoTratada_RunPermaneceTerminalAteUmStartExplicito()
    {
        TaskRegistry.Dispatch(["""{"type":"tool","value":"boom"}"""], FaultyTasks);

        // A subsequent, non-start turn keeps refusing — same recoverable-only-via-"start"
        // contract as timeout/budget.
        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], FaultyTasks);

        Assert.Equal("PROMPT_START", result);
        Assert.Null(StateStore.TerminalReason());
    }

    [Fact]
    public void Dispatch_LogaEntradaAntesDaActionRodarESaidaDepoisDeConcluir()
    {
        var logPath = Path.Combine(".harness", "harness.log");
        if (File.Exists(logPath))
            File.Delete(logPath);

        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["Login"]}"""], Tasks);

        var lines = File.ReadAllLines(logPath);
        var enterIndex = Array.FindIndex(lines, l => l.Contains("enter 'classify'"));
        var exitIndex = Array.FindIndex(lines, l => l.Contains("exit outcome="));

        Assert.True(enterIndex >= 0, "expected an 'enter' line in harness.log");
        Assert.True(exitIndex >= 0, "expected an 'exit' line in harness.log");
        Assert.True(enterIndex < exitIndex, "entry must be logged before exit");
    }
}
