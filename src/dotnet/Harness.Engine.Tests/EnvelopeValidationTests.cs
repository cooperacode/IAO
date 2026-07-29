namespace Harness.Engine.Tests;

/// <summary>
/// Contextual validation (Phase 4): the right command with a VALUE outside expectations
/// becomes a typed corrective error — never a silent "stop", never persists bad content.
/// </summary>
public class EnvelopeValidationTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
    };

    private static readonly Dictionary<string, Func<Envelope, ValidationResult>> Validators = new()
    {
        ["classify"] = EnvelopeValidation.NotEmpty("the item's description"),
    };

    public EnvelopeValidationTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void Dispatch_ValorReprovado_RetornaErroCorretivoENaoExecutaATask()
    {
        var result = TaskRegistry.Dispatch(
            ["""{"type":"tool","value":"classify"}"""], Tasks, Validators);

        Assert.StartsWith("HARNESS PROTOCOL ERROR", result);
        Assert.NotEqual("stop", result);
        Assert.Contains("was rejected", result);
        Assert.DoesNotContain("PROMPT_CLASSIFY", result);
    }

    [Fact]
    public void Dispatch_ValorAprovado_ExecutaATaskNormalmente()
    {
        var result = TaskRegistry.Dispatch(
            ["""{"type":"tool","value":"classify","args":["Login"]}"""], Tasks, Validators);

        Assert.Equal("PROMPT_CLASSIFY:Login", result);
    }

    [Fact]
    public void Dispatch_ComandoSemValidador_NaoEValidado()
    {
        var validators = new Dictionary<string, Func<Envelope, ValidationResult>>();

        var result = TaskRegistry.Dispatch(
            ["""{"type":"tool","value":"classify"}"""], Tasks, validators);

        Assert.StartsWith("PROMPT_CLASSIFY", result);
    }

    [Fact]
    public void MinLines_ContaQuebrasLiteraisEEscapadas()
    {
        var validator = EnvelopeValidation.MinLines(2, "story list");

        // Artifacts travel as a single-line string with literal \n (the "Compact" notice).
        var escaped = new Envelope("tool", "acceptance", [@"1. a\n2. b"]);
        var real = new Envelope("tool", "acceptance", ["1. a\n2. b"]);
        var single = new Envelope("tool", "acceptance", ["1. a"]);

        Assert.True(validator(escaped).Ok);
        Assert.True(validator(real).Ok);
        Assert.False(validator(single).Ok);
    }

    [Fact]
    public void ContainsNumber_ExigeAoMenosUmDigito()
    {
        var validator = EnvelopeValidation.ContainsNumber("estimates");

        Assert.True(validator(new Envelope("tool", "risks", ["5 points"])).Ok);
        Assert.False(validator(new Envelope("tool", "risks", ["no points"])).Ok);
    }

    [Fact]
    public void Matches_CasaSemDiferenciarMaiusculas()
    {
        var validator = EnvelopeValidation.Matches("READY|NOT READY", "DoR verdict");

        Assert.True(validator(new Envelope("tool", "finalize", ["Verdict: ready with caveat"])).Ok);
        Assert.False(validator(new Envelope("tool", "finalize", ["approved"])).Ok);
    }

    [Fact]
    public void Matches_ComPadraoAncorado_RejeitaConteudoQueApenasContemOPrefixo()
    {
        var validator = EnvelopeValidation.Matches(@"^(PASS\b|FAIL\b)", "verdict");

        Assert.True(validator(new Envelope("command", "verify", ["PASS: tests green"])).Ok);
        Assert.True(validator(new Envelope("command", "verify", ["FAIL: tests red"])).Ok);
        Assert.False(validator(new Envelope("command", "verify", ["ran the tests and got PASS"])).Ok);
    }

    [Fact]
    public void All_FalhaNaPrimeiraRazao()
    {
        var validator = EnvelopeValidation.All(
            EnvelopeValidation.NotEmpty("estimates"),
            EnvelopeValidation.ContainsNumber("estimates with points"));

        var result = validator(new Envelope("tool", "risks", ["no numbers"]));

        Assert.False(result.Ok);
        Assert.Contains("number", result.Reason);
    }

    [Fact]
    public void Parse_IgnoraCamposDesconhecidos()
    {
        // Extra fields (e.g. a "tokens" field from an old driver) don't break parsing.
        var envelope = Envelope.Parse("""{"type":"tool","value":"classify","args":["x"],"tokens":1234}""");

        Assert.NotNull(envelope);
        Assert.Equal("classify", envelope!.Value);
    }
}
