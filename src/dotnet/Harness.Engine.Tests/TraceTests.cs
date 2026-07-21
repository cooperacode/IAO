using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// O trace é a sequência de comandos que o StateStore não guarda (ele sobrescreve o
/// estado). Sem ele não há Trajectory Evaluation nem Telemetria de custo por passo.
/// </summary>
public class TraceTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
        ["finalize"] = _ => "stop",
    };

    public TraceTests()
    {
        StateStore.Reset();
        Trace.Reset();
    }

    public void Dispose()
    {
        StateStore.Reset();
        Trace.Reset();
    }

    [Fact]
    public void AppendELoad_FazemRoundtripNaOrdemDeGravacao()
    {
        var before = DateTimeOffset.UtcNow;
        Trace.Append(1, "start", TraceOutcome.Instruction, 42);
        Trace.Append(2, "classify", TraceOutcome.Instruction, 99);
        var after = DateTimeOffset.UtcNow;

        var entries = Trace.Load();

        Assert.Equal(2, entries.Count);
        Assert.Equal((1, "start", TraceOutcome.Instruction, 42), (entries[0].Step, entries[0].Command, entries[0].Outcome, entries[0].InstructionChars));
        Assert.Equal((2, "classify", TraceOutcome.Instruction, 99), (entries[1].Step, entries[1].Command, entries[1].Outcome, entries[1].InstructionChars));
        Assert.InRange(entries[0].Timestamp, before, after);
        Assert.InRange(entries[1].Timestamp, before, after);
    }

    [Fact]
    public void Load_SemArquivo_RetornaVazio()
    {
        Assert.Empty(Trace.Load());
    }

    [Fact]
    public void Dispatch_GravaOComandoEODesfechoDeCadaPasso()
    {
        TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);
        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["Login"]}"""], Tasks);
        TaskRegistry.Dispatch(["""{"type":"command","value":"finalize"}"""], Tasks);

        var entries = Trace.Load();

        Assert.Equal(["start", "classify", "finalize"], entries.Select(e => e.Command));
        Assert.Equal(
            [TraceOutcome.Instruction, TraceOutcome.Instruction, TraceOutcome.Stop],
            entries.Select(e => e.Outcome));
    }

    [Fact]
    public void Dispatch_Start_TruncaOTraceAnterior()
    {
        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);
        Assert.Equal(2, Trace.Load().Count);

        TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        var entries = Trace.Load();
        Assert.Single(entries);
        Assert.Equal("start", entries[0].Command);
        Assert.Equal(1, entries[0].Step);
    }

    [Fact]
    public void Dispatch_JsonMalformado_GravaComandoUnparsedComDesfechoError()
    {
        TaskRegistry.Dispatch(["""{"type":"text","value":"""], Tasks);

        var entry = Assert.Single(Trace.Load());
        Assert.Equal("(unparsed)", entry.Command);
        Assert.Equal(TraceOutcome.Error, entry.Outcome);
    }

    [Fact]
    public void Dispatch_AoExcederOTeto_GravaDesfechoBudget()
    {
        for (var i = 0; i < TaskRegistry.MaxSteps; i++)
            TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        TaskRegistry.Dispatch(["""{"type":"tool","value":"classify","args":["x"]}"""], Tasks);

        var last = Trace.Load()[^1];
        Assert.Equal(TraceOutcome.Budget, last.Outcome);
        Assert.Equal(TaskRegistry.MaxSteps + 1, last.Step);
    }
}
