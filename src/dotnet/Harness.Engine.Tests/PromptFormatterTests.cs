using Harness.Engine;

namespace Harness.Engine.Tests;

public class PromptFormatterTests : IDisposable
{
    public PromptFormatterTests() => StateStore.Reset();
    public void Dispose() => StateStore.Reset();

    [Fact]
    public void Skills_AceitaVariosNomes_RetornaTodosOsMapeamentos()
    {
        var skills = PromptFormatter.Skills("agile-workitem", "story-splitting");

        Assert.Equal(2, skills.Count);
        Assert.Equal(Path.Combine("skills", "agile-workitem", "SKILL.md"), skills["agile-workitem"]);
        Assert.Equal(Path.Combine("skills", "story-splitting", "SKILL.md"), skills["story-splitting"]);
    }

    [Fact]
    public void Format_ContextoPersistido_EReinjetadoNoEnvelopeDeSaida()
    {
        StateStore.SetContext(new Dictionary<string, string> { ["driver"] = "claude code" });
        var output = new Envelope(EnvelopeType.Command, "plan", []);

        var result = PromptFormatter.Format("faça algo", output);

        Assert.Contains("\"context\":{\"driver\":\"claude code\"}", result);
    }

    [Fact]
    public void Format_SemContextoPersistido_NaoEmiteOCampo()
    {
        var output = new Envelope(EnvelopeType.Command, "plan", []);

        var result = PromptFormatter.Format("faça algo", output);

        Assert.DoesNotContain("context", result);
    }

    [Fact]
    public void Format_ContextoJaDefinidoNaTask_NaoESobrescrito()
    {
        StateStore.SetContext(new Dictionary<string, string> { ["driver"] = "claude code" });
        var output = new Envelope(EnvelopeType.Command, "plan", [])
        {
            Context = new Dictionary<string, string> { ["driver"] = "explicito" },
        };

        var result = PromptFormatter.Format("faça algo", output);

        Assert.Contains("explicito", result);
        Assert.DoesNotContain("claude code", result);
    }
}
