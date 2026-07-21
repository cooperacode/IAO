namespace Harness.Engine.Tests;

/// <summary>
/// Validação contextual (Fase 4): comando certo com VALOR fora da expectativa vira erro
/// corretivo tipado — nunca "stop" silencioso, nunca persiste conteúdo ruim.
/// </summary>
public class EnvelopeValidationTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
    };

    private static readonly Dictionary<string, Func<Envelope, ValidationResult>> Validators = new()
    {
        ["classify"] = EnvelopeValidation.NotEmpty("a descrição do item"),
    };

    public EnvelopeValidationTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void Dispatch_ValorReprovado_RetornaErroCorretivoENaoExecutaATask()
    {
        var result = TaskRegistry.Dispatch(
            ["""{"type":"tool","value":"classify"}"""], Tasks, Validators);

        Assert.StartsWith("ERRO", result);
        Assert.NotEqual("stop", result);
        Assert.Contains("recusado", result);
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
        var validator = EnvelopeValidation.MinLines(2, "lista de histórias");

        // Artefatos trafegam como string de uma linha com \n literais (aviso "Compact").
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
        var validator = EnvelopeValidation.ContainsNumber("estimativas");

        Assert.True(validator(new Envelope("tool", "risks", ["5 pontos"])).Ok);
        Assert.False(validator(new Envelope("tool", "risks", ["sem pontos"])).Ok);
    }

    [Fact]
    public void Matches_CasaSemDiferenciarMaiusculas()
    {
        var validator = EnvelopeValidation.Matches("READY|NOT READY", "veredito do DoR");

        Assert.True(validator(new Envelope("tool", "finalize", ["Veredito: ready com ressalva"])).Ok);
        Assert.False(validator(new Envelope("tool", "finalize", ["aprovado"])).Ok);
    }

    [Fact]
    public void Matches_ComPadraoAncorado_RejeitaConteudoQueApenasContemOPrefixo()
    {
        var validator = EnvelopeValidation.Matches(@"^(PASS\b|FAIL\b)", "veredito");

        Assert.True(validator(new Envelope("command", "verify", ["PASS: testes verdes"])).Ok);
        Assert.True(validator(new Envelope("command", "verify", ["FAIL: testes vermelhos"])).Ok);
        Assert.False(validator(new Envelope("command", "verify", ["rodei os testes e deu PASS"])).Ok);
    }

    [Fact]
    public void All_FalhaNaPrimeiraRazao()
    {
        var validator = EnvelopeValidation.All(
            EnvelopeValidation.NotEmpty("estimativas"),
            EnvelopeValidation.ContainsNumber("estimativas com pontos"));

        var result = validator(new Envelope("tool", "risks", ["sem numeros"]));

        Assert.False(result.Ok);
        Assert.Contains("número", result.Reason);
    }

    [Fact]
    public void Parse_IgnoraCamposDesconhecidos()
    {
        // Campos extras (ex.: um "tokens" de driver antigo) não derrubam o parse.
        var envelope = Envelope.Parse("""{"type":"tool","value":"classify","args":["x"],"tokens":1234}""");

        Assert.NotNull(envelope);
        Assert.Equal("classify", envelope!.Value);
    }
}
