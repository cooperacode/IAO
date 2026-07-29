using Harness.Engine;

namespace Harness.Engine.Tests;

/// <summary>
/// Inbox transport: with empty argv, dispatch reads the envelope from
/// <c>.harness/inbox.json</c> — the channel that eliminates the shell-quoting hang (the
/// driver writes a file instead of assembling a single-quoted argument). Argv still takes
/// precedence (backward compatible).
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
        // The case that used to hang the shell: a payload with single quotes and line
        // breaks. Via file, it arrives intact without fragile escaping.
        WriteInbox("""{ "type": "command", "value": "classify", "args": ["export 'PDF'\nand 'CSV'"] }""");

        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.Equal("PROMPT_CLASSIFY:export 'PDF'\nand 'CSV'", result);
    }

    [Fact]
    public void Dispatch_DaInbox_ConsomeOArquivoAposParse()
    {
        WriteInbox("""{ "type": "text", "value": "start" }""");

        TaskRegistry.Dispatch([], Tasks);

        Assert.False(File.Exists(Inbox.Path), "the inbox should be moved after a successful parse");
        Assert.True(File.Exists(Inbox.ConsumedPath), "the consumed envelope should remain as a trail");
    }

    [Fact]
    public void Dispatch_InboxInvalida_RetornaErroEnaoConsome()
    {
        WriteInbox("""{ "type": "text", "value": """);

        var result = TaskRegistry.Dispatch([], Tasks);

        Assert.StartsWith("HARNESS PROTOCOL ERROR", result);
        // Broken JSON remains available for inspection — it doesn't silently disappear.
        Assert.True(File.Exists(Inbox.Path), "an inbox that fails to parse must not be consumed");
    }

    [Fact]
    public void Dispatch_ArgvTemPrecedenciaSobreInbox()
    {
        // Argv present → classic transport; the inbox is ignored and stays intact.
        WriteInbox("""{ "type": "command", "value": "classify", "args": ["from-inbox"] }""");

        var result = TaskRegistry.Dispatch(["""{"type":"text","value":"start"}"""], Tasks);

        Assert.Equal("PROMPT_START", result);
        Assert.True(File.Exists(Inbox.Path), "with argv, the inbox must not be touched");
    }
}
