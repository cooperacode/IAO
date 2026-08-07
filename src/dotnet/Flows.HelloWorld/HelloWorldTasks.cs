using Harness.Engine;

namespace Flows.HelloWorld;

/// <summary>
/// Teaching flow: the smallest possible IAO round trip. Three deterministic
/// steps — no real reasoning required from the driver, just echoing the
/// requested word back — so the protocol itself (stdout instruction in,
/// JSON envelope out) is the whole lesson.
/// start → ping → pong → stop
/// </summary>
public static class HelloWorldTasks
{
    public static string Start() =>
        PromptFormatter.Format(
            "Reply with exactly the word \"ping\" — no reasoning needed, just echo it back.",
            new Envelope(EnvelopeType.Text, "ping", []));

    public static string Ping(Envelope? envelope) =>
        PromptFormatter.Format(
            $"You said '{envelope?.Value}'. Now reply with exactly the word \"pong\".",
            new Envelope(EnvelopeType.Text, "pong", []));

    public static string Pong(Envelope? envelope) => "stop";
}
