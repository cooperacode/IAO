using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Transporte por inbox: com argv vazio, o dispatch lê o envelope de
/// <c>.harness/inbox.json</c> — o canal que elimina o hang de aspas do shell (o driver
/// escreve um arquivo em vez de montar um argumento single-quoted). Argv continua tendo
/// precedência (retrocompatível).
/// </summary>
public class InboxTests : IDisposable
{
    private static readonly Dictionary<string, Func<Envelope?, string>> Tasks = new()
    {
        ["start"] = _ => "PROMPT_START",
        ["classify"] = e => $"PROMPT_CLASSIFY:{e?.Args?.FirstOrDefault()}",
    };

    public InboxTests()
    {
        StateStore.Reset();
        ClearInbox();
    }

    public void Dispose()
    {
        StateStore.Reset();
        ClearInbox();
    }

    private static void ClearInbox()
    {
        File.Delete(Inbox.Path);
        File.Delete(Inbox.ConsumedPath);
    }

    private static void WriteInbox(string json)
    {
        Directory.CreateDirectory(".harness");
        File.WriteAllText(Inbox.Path, json);
    }

    [Fact]
    public void Dispatch_SemArgumento_LeEnvelopeDaInbox()
    {
        WriteInbox("""{ "type": "text", "value": "start" }""");

        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.Equal("PROMPT_START", result);
    }

    [Fact]
    public void Dispatch_DaInbox_PreservaOsArgs()
    {
        // O caso que travava o shell: payload com aspas simples e quebras de linha. Via
        // arquivo, chega íntegro sem escaping frágil.
        WriteInbox("""{ "type": "command", "value": "classify", "args": ["exportar 'PDF'\ne 'CSV'"] }""");

        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.Equal("PROMPT_CLASSIFY:exportar 'PDF'\ne 'CSV'", result);
    }

    [Fact]
    public void Dispatch_DaInbox_ConsomeOArquivoAposParse()
    {
        WriteInbox("""{ "type": "text", "value": "start" }""");

        TaskRegistry.Dispatch([], Tasks);

        Assert.False(File.Exists(Inbox.Path), "a inbox deve ser movida após um parse bem-sucedido");
        Assert.True(File.Exists(Inbox.ConsumedPath), "o envelope consumido deve ficar como rastro");
    }

    [Fact]
    public void Dispatch_InboxInvalida_RetornaErroEnaoConsome()
    {
        WriteInbox("""{ "type": "text", "value": """);

        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.StartsWith("ERRO", result);
        // JSON quebrado permanece disponível para inspeção — não some silenciosamente.
        Assert.True(File.Exists(Inbox.Path), "uma inbox que não parseia não deve ser consumida");
    }

    [Fact]
    public void Dispatch_ArgvTemPrecedenciaSobreInbox()
    {
        // Argv presente → transporte clássico; a inbox é ignorada e permanece intacta.
        WriteInbox("""{ "type": "command", "value": "classify", "args": ["da-inbox"] }""");

        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.Equal("PROMPT_START", result);
        Assert.True(File.Exists(Inbox.Path), "com argv, a inbox não deve ser tocada");
    }
}
