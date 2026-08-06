using System.Text;

namespace Harness.Engine;

/// <summary>Domain-agnostic dispatch: envelope parsing, iteration guard, and typed error.</summary>
public static class TaskRegistry
{
    // Step ceiling: prevents an infinite loop that would burn tokens indefinitely.
    // Value comes from harness.json (or its default) — see HarnessConfig.
    public static int MaxSteps => HarnessConfig.Current.MaxSteps;

    public static string Dispatch(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, Func<Envelope?, string>> actions,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators = null,
        int? maxSteps = null,
        Func<bool>? shouldResetOnStart = null)
    {
        // Argv present → classic transport (backward compatible). Empty argv → reads the
        // envelope from the file-based inbox, the transport that eliminates the shell-quoting
        // hang (see Inbox).
        var fromInbox = args.Count == 0;
        var arg0 = args.Count >= 1 ? args[0] : Inbox.Read();

        var envelope = string.IsNullOrWhiteSpace(arg0)
            ? null
            : Envelope.Parse(arg0);

        // Only consumes the inbox when parsing succeeded — a broken JSON must produce the
        // corrective ERROR and remain available for inspection, not silently disappear.
        if (fromInbox && envelope is not null)
            Inbox.Consume();

        // Budget stops remain terminal. A timeout or fault is recoverable only through an
        // explicit `start`: the abandoned worker (timed out, or crashed on a harness bug)
        // belonged to the previous process, and the driver is deliberately asking the flow
        // to resume or restart — never by silently resending the same command.
        var terminal = StateStore.TerminalReason();
        if (terminal is not null)
        {
            if (terminal is "timeout" or "fault" && envelope?.Value == "start")
                StateStore.ClearTerminal();
            else
            {
                HarnessLog.Error($"[harness] run already stopped ({terminal}); refusing another turn.");
                return "stop";
            }
        }

        if (envelope is not null && envelope.Value == "start")
        {
            // A new workflow starts from scratch — state and trace are truncated together. But
            // a "start" also arrives when a fresh session (e.g. a Development per-feature hard
            // reset) reopens a run in progress — in that case it's a RESUME, not a start, and
            // truncating here would throw away the trace/step accumulated by previous
            // features. The flow decides via shouldResetOnStart (it knows whether there's
            // pending work); with no predicate, the default is to always reset (backward
            // compatible with single-shot flows).
            if (shouldResetOnStart?.Invoke() ?? true)
            {
                StateStore.Reset();
                Trace.Reset();
                HarnessLog.Reset();
            }

            // The driver context (e.g. {"driver":"claude code"}) is born here and survives in
            // StateStore — PromptFormatter reinjects it into every output until the next
            // "start". Independent of the reset above: even on a resume, the current driver
            // must prevail.
            if (envelope.Context is { Count: > 0 } context)
                StateStore.SetContext(context);
        }

        var observedContextUsage = envelope?.ContextUsage ?? ContextUsage.FromEnvironment();
        ContextPolicy.Observe(observedContextUsage);

        // Iteration guard — hard stop under the team's token budget.
        var step = StateStore.Increment();

        var costChars = StateStore.Load().CostChars;
        var command = envelope?.Value is { Length: > 0 } value ? value : "(unparsed)";

        // Logged BEFORE the action runs: trace.jsonl only gets a line once the step
        // completes, so a slow or hung step (or one that crashes below) would otherwise
        // leave zero evidence the harness ever picked it up — the "feels idle" gap.
        HarnessLog.Info($"[step {step}] enter '{command}'");

        var (result, outcome) = Resolve(envelope, step, costChars, actions, validators, maxSteps);

        // UTF-8 octets, not .NET chars (RFC Appendix B item 1): measures what actually crosses
        // the transport, with the same meaning as Python (len(bytes)) and Rust (String::len()).
        var resultBytes = Encoding.UTF8.GetByteCount(result);

        HarnessLog.Info($"[step {step}] exit outcome={outcome} bytes={resultBytes}");

        // One line per loop turn: feeds telemetry and the trajectory evaluator. Label is
        // re-read (not from the Load() snapshot above) because the action itself may have
        // just set it (e.g. Pick() choosing this step's feature).
        var label = StateStore.Get(StateStore.TraceLabelKey) ?? "";
        Trace.Append(step, command, outcome, resultBytes, label, observedContextUsage);

        // The instruction's cost is only known here now — it feeds the accumulator the next
        // turn's guard will check.
        StateStore.AddCost(resultBytes);
        return result;
    }

    private static (string Result, string Outcome) Resolve(
        Envelope? envelope, int step, int costChars,
        IReadOnlyDictionary<string, Func<Envelope?, string>> actions,
        IReadOnlyDictionary<string, Func<Envelope, ValidationResult>>? validators,
        int? maxSteps = null)
    {
        // Effective step ceiling: a per-call override (e.g. a long-running flow like
        // Development, which needs more slack) takes precedence over harness.json's global
        // one. With no override, the config's value applies — Refinement/Evaluation stay
        // unchanged.
        var effectiveMaxSteps = maxSteps ?? MaxSteps;
        if (step > effectiveMaxSteps)
        {
            HarnessLog.Error($"[harness] step limit of {effectiveMaxSteps} reached; stopping.");
            StateStore.MarkTerminal("budget");
            return ("stop", TraceOutcome.Budget);
        }

        // Cost ceiling, a second guard beyond the step one. Emitted-instruction chars are the
        // only measure: it's what the engine can attest on its own. Real tokens live in the
        // caller's billing metadata — an LLM driver has no way to honestly report them.
        var config = HarnessConfig.Current;
        if (config.MaxInstructionChars > 0 && costChars > config.MaxInstructionChars)
        {
            HarnessLog.Error(
                $"[harness] instruction char limit of {config.MaxInstructionChars} reached ({costChars}); stopping.");
            StateStore.MarkTerminal("budget");
            return ("stop", TraceOutcome.Budget);
        }

        // Typed error instead of silent "stop": the model receives the cause and can resend
        // the right command (corrective loop, not silent termination).
        if (envelope is null)
            return (ErrorInstruction("Could not parse the received JSON.", actions), TraceOutcome.Error);

        if (!actions.TryGetValue(envelope.Value, out var action))
            return (ErrorInstruction($"The command '{envelope.Value}' does not exist.", actions), TraceOutcome.Error);

        // Contextual validation: the command exists, but does the VALUE meet the task's
        // expectation? Failed → same corrective-error path as above; the driver fixes and resends.
        if (validators is not null
            && validators.TryGetValue(envelope.Value, out var validator)
            && validator(envelope) is { Ok: false } rejected)
        {
            return (ErrorInstruction(
                $"The command '{envelope.Value}' was rejected: {rejected.Reason} "
                + "Fix the 'args' content and resend the same command.", actions), TraceOutcome.Error);
        }

        // Time guard: a stuck task (infinite loop in domain logic) would hang the process
        // indefinitely. RunWithTimeout enforces the per-step ceiling; a timeout becomes a
        // typed error, caught here, following the same graceful path as the budget cut:
        // stderr diagnostic + "stop" on stdout (the channel the IDE client reads).
        try
        {
            var result = RunWithTimeout(action, envelope, HarnessConfig.Current.TimeoutMs);
            return (result, result == "stop" ? TraceOutcome.Stop : TraceOutcome.Instruction);
        }
        catch (HarnessTimeoutException ex)
        {
            HarnessLog.Error($"[harness] {ex.Message}");
            StateStore.MarkTerminal("timeout");
            return ("stop", TraceOutcome.Timeout);
        }
        catch (Exception ex)
        {
            // A bug in the task action itself (not a driver protocol error) — must not
            // crash the process silently. Logged in full, then the same graceful
            // termination shape as budget/timeout: stderr+log diagnostic, "stop" on
            // stdout, recoverable only via an explicit "start" (see TerminalReason()).
            HarnessLog.Error($"[harness] unhandled fault in command '{envelope.Value}': {ex}");
            StateStore.MarkTerminal("fault");
            return ("stop", TraceOutcome.Fault);
        }
    }

    // The task is a synchronous, OPAQUE Func — it does not cooperate with CancellationToken.
    // Modern .NET cannot safely abort stuck synchronous code (Thread.Abort was removed), so
    // the only real preemptive timeout is to run it on another thread and ABANDON whatever
    // hangs. Task.Run uses the threadpool (background threads): when the single-shot process
    // exits with "stop", it terminates even with the runaway task still running — a
    // foreground new Thread would block termination. GetAwaiter().GetResult() (not .Result)
    // rethrows the task's original exception without wrapping it in AggregateException,
    // preserving current behavior.
    private static string RunWithTimeout(Func<Envelope?, string> action, Envelope? envelope, int timeoutMs)
    {
        if (timeoutMs <= 0)
            return action(envelope); // guard disabled — no thread overhead

        var task = Task.Run(() => action(envelope));
        if (!task.Wait(timeoutMs))
            throw new HarnessTimeoutException(timeoutMs);
        return task.GetAwaiter().GetResult(); // task already completed here — doesn't block; only rethrows
    }

    private static string ErrorInstruction(string reason, IReadOnlyDictionary<string, Func<Envelope?, string>> actions)
    {
        var valid = string.Join(", ", actions.Keys);
        return $"HARNESS PROTOCOL ERROR: {reason} Valid commands: {valid}. "
            + "Review the 'value' field in your JSON response (reply with the JSON only, "
            + "no code fences or commentary) and resend the command.";
    }
}
