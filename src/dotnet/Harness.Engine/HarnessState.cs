namespace Harness.Engine;

/// <summary>
/// State persisted across invocations: step counter + accumulated domain data.
/// Top-level type (not nested) so it's servable by System.Text.Json's source generator, a
/// Native AOT requirement.
/// </summary>
public record HarnessState(int Step, Dictionary<string, string> Data)
{
    // Run's accumulated cost, input for the cost ceiling (see TaskRegistry). init
    // properties (non-positional) so they don't break the existing `new HarnessState(0, new())`
    // calls; kept out of Data so it doesn't pollute the evaluation's completeness check.

    /// <summary>Instruction chars emitted so far — the cost proxy (sum of <c>InstructionChars</c>).</summary>
    public int CostChars { get; init; }

    /// <summary>
    /// Driver context (e.g. <c>{"driver":"claude code"}</c>) captured in the <c>start</c>
    /// envelope — survives across invocations so <see cref="PromptFormatter"/> can
    /// reinject it into every output without each task passing it along manually.
    /// </summary>
    public Dictionary<string, string>? Context { get; init; }
}
