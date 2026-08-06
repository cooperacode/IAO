using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>Data contract: parsing needs to tolerate what models actually return.</summary>
public class EnvelopeTests
{
    [Fact]
    public void Parse_JsonValido_PreencheOsTresCampos()
    {
        var envelope = Envelope.Parse("""{"type":"tool","value":"classify","args":["Login"]}""");

        Assert.NotNull(envelope);
        Assert.Equal("tool", envelope.Type);
        Assert.Equal("classify", envelope.Value);
        Assert.Equal(["Login"], envelope.Args!);
    }

    [Fact]
    public void Parse_ComCercaMarkdown_Tolera()
    {
        var raw = """
        ```json
        {"type":"command","value":"finalize","args":["Bug"]}
        ```
        """;

        var envelope = Envelope.Parse(raw);

        Assert.NotNull(envelope);
        Assert.Equal("finalize", envelope.Value);
        Assert.Equal(["Bug"], envelope.Args!);
    }

    [Fact]
    public void Parse_ComTextoAoRedor_ExtraiOObjeto()
    {
        var raw = """Sure! Here it is: {"type":"text","value":"start","args":[]} — hope that helps.""";

        var envelope = Envelope.Parse(raw);

        Assert.NotNull(envelope);
        Assert.Equal("start", envelope.Value);
    }

    [Fact]
    public void Parse_SemArgs_RetornaArrayVazio()
    {
        var envelope = Envelope.Parse("""{"type":"text","value":"start"}""");

        Assert.NotNull(envelope);
        Assert.Empty(envelope.Args!);
    }

    [Fact]
    public void Parse_IgnoraArgsVaziosOuEmBranco()
    {
        var envelope = Envelope.Parse("""{"type":"tool","value":"x","args":["a","","  ","b"]}""");

        Assert.NotNull(envelope);
        Assert.Equal(["a", "b"], envelope.Args!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"type\": \"text\", \"value\": ")]   // truncated JSON
    [InlineData("this is not json")]
    [InlineData("[1,2,3]")]                             // not an object
    public void Parse_EntradaInvalida_RetornaNull(string raw)
    {
        Assert.Null(Envelope.Parse(raw));
    }

    [Fact]
    public void Parse_EntradaInvalida_GravaOPayloadCruNoHarnessLog()
    {
        // The raw driver payload is otherwise lost forever — the inbox file gets
        // overwritten by the next attempt before anyone can inspect what actually failed.
        const string logPath = ".harness/harness.log";
        if (File.Exists(logPath))
            File.Delete(logPath);

        Envelope.Parse("this is not json");

        var content = File.ReadAllText(logPath);
        Assert.Contains("this is not json", content);
    }

    [Fact]
    public void Parse_PayloadMaiorQueOTeto_TruncaNoHarnessLog()
    {
        const string logPath = ".harness/harness.log";
        if (File.Exists(logPath))
            File.Delete(logPath);

        var oversized = new string('x', 600) + "not json";
        Envelope.Parse(oversized);

        var content = File.ReadAllText(logPath);
        Assert.Contains("...(truncated)", content);
        Assert.DoesNotContain(oversized, content);
    }

    [Fact]
    public void ToJson_FazRoundtrip()
    {
        var original = new Envelope(EnvelopeType.Command, "finalize", ["Epic"]);

        var roundtrip = Envelope.Parse(original.ToJson());

        Assert.Equal(original, roundtrip);
    }

    [Fact]
    public void Parse_ComContext_PreencheODicionario()
    {
        var envelope = Envelope.Parse(
            """{"type":"text","value":"start","context":{"driver":"claude code"}}""");

        Assert.NotNull(envelope);
        Assert.Equal("claude code", envelope.Context?["driver"]);
    }

    [Fact]
    public void Parse_SemContext_RetornaNull()
    {
        var envelope = Envelope.Parse("""{"type":"text","value":"start"}""");

        Assert.NotNull(envelope);
        Assert.Null(envelope.Context);
    }

    [Fact]
    public void ToJson_ComContext_FazRoundtrip()
    {
        var original = new Envelope(EnvelopeType.Text, "start", [])
        {
            Context = new Dictionary<string, string> { ["driver"] = "claude code" },
        };

        var roundtrip = Envelope.Parse(original.ToJson());

        Assert.Equal(original, roundtrip);
        Assert.Equal("claude code", roundtrip!.Context?["driver"]);
    }

    [Fact]
    public void ToJson_SemContext_NaoEmiteOCampo()
    {
        var envelope = new Envelope(EnvelopeType.Command, "finalize", ["Epic"]);

        Assert.DoesNotContain("context", envelope.ToJson());
    }

    [Fact]
    public void ContextUsage_FazRoundtrip()
    {
        var original = new Envelope(EnvelopeType.Command, "start", [])
        {
            ContextUsage = new ContextUsage("iao.context.v1", "s1", 128_000, 84_000, "driver"),
        };

        var roundtrip = Envelope.Parse(original.ToJson());

        Assert.Equal(original, roundtrip);
    }
}
