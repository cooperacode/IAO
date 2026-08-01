"""Domain-agnostic dispatch: envelope parsing, iteration guard, and typed error handling."""

from __future__ import annotations

import sys
import threading
from typing import Callable, Mapping

from harness_engine import context_policy, harness_config, inbox, state_store, trace
from harness_engine.envelope import Envelope
from harness_engine.envelope_validation import ValidationResult
from harness_engine.errors import HarnessTimeoutError

Action = Callable[["Envelope | None"], str]
Validator = Callable[[Envelope], ValidationResult]


def default_max_steps() -> int:
    """Step ceiling: prevents an infinite loop that would burn tokens indefinitely.
    Value comes from harness.json (or the default) — see harness_config."""
    return harness_config.current().max_steps


def dispatch(
    args: list[str],
    actions: Mapping[str, Action],
    validators: Mapping[str, Validator] | None = None,
    max_steps: int | None = None,
    should_reset_on_start: Callable[[], bool] | None = None,
) -> str:
    # Argv present → classic transport (backward compatible). Empty argv → reads the
    # envelope from the file-based inbox, the transport that eliminates the shell-quoting
    # hang (see inbox).
    from_inbox = len(args) == 0
    arg0 = args[0] if len(args) >= 1 else inbox.read()

    envelope = Envelope.parse(arg0) if arg0 and arg0.strip() else None

    # Only consume the inbox when parsing succeeded — a broken JSON must produce the
    # corrective ERROR and remain available for inspection, not silently disappear.
    if from_inbox and envelope is not None:
        inbox.consume()

    # Budget stops remain terminal. A timeout is recoverable only through an explicit
    # `start`: the timed-out worker was abandoned with the previous process, and the
    # driver is deliberately asking the flow to resume or restart.
    terminal = state_store.terminal_reason()
    if terminal is not None:
        if terminal == "timeout" and envelope is not None and envelope.value == "start":
            state_store.clear_terminal()
        else:
            print(f"[harness] run already stopped ({terminal}); refusing another turn.", file=sys.stderr)
            return "stop"

    if envelope is not None and envelope.value == "start":
        # A new workflow starts from scratch — state and trace are truncated together. But a
        # "start" also arrives when a fresh session (e.g. a Development per-feature hard
        # reset) reopens a run in progress — in that case it's a RESUME, not a start, and
        # truncating here would throw away the trace/step accumulated by previous features.
        # The flow decides via should_reset_on_start (it knows whether there's pending
        # work); with no predicate, the default is to always reset (backward compatible
        # with single-shot flows).
        if should_reset_on_start is None or should_reset_on_start():
            state_store.reset()
            trace.reset()

        # The driver context (e.g. {"driver":"claude code"}) is born here and survives in
        # state_store — prompt_formatter reinjects it into every output until the next
        # "start". Independent of the reset above: even on a resume, the current driver
        # must prevail.
        if envelope.context:
            state_store.set_context(envelope.context)

    observed_context_usage = (
        envelope.context_usage if envelope is not None and envelope.context_usage is not None
        else context_policy.ContextUsage.from_environment()
    )
    context_policy.observe(observed_context_usage)

    # Iteration guard — hard stop under the team's token budget.
    step = state_store.increment()

    cost_chars = state_store.load().cost_chars
    command = envelope.value if envelope is not None and envelope.value else "(unparsed)"

    result, outcome = _resolve(envelope, step, cost_chars, actions, validators, max_steps)

    # UTF-8 octets, not codepoints (RFC Appendix B item 1) — the same unit across every
    # engine (.NET/Python/Rust), so the cost ceiling means the same thing cross-engine
    # regardless of accents/emoji in the emitted instruction.
    result_bytes = len(result.encode("utf-8"))

    # One line per loop turn: feeds telemetry and the trajectory evaluator. Label is
    # re-read (not from the load() snapshot above) because the action itself may have
    # just set it (e.g. pick() choosing this step's feature).
    label = state_store.get(state_store.TRACE_LABEL_KEY) or ""
    trace.append(step, command, outcome, result_bytes, label, observed_context_usage)

    # The emitted instruction's cost is only known here now — it feeds the accumulator
    # the next turn's guard will check.
    state_store.add_cost(result_bytes)
    return result


def _resolve(
    envelope: Envelope | None,
    step: int,
    cost_chars: int,
    actions: Mapping[str, Action],
    validators: Mapping[str, Validator] | None,
    max_steps: int | None,
) -> tuple[str, str]:
    # Effective step ceiling: the per-call override (e.g. a long-running flow like
    # Development, which needs more slack) takes precedence over harness.json's global one.
    effective_max_steps = max_steps if max_steps is not None else default_max_steps()
    if step > effective_max_steps:
        print(f"[harness] step limit of {effective_max_steps} reached; stopping.", file=sys.stderr)
        state_store.mark_terminal("budget")
        return "stop", trace.TraceOutcome.BUDGET

    # Cost ceiling, a second guard beyond the step one. Emitted-instruction chars are the
    # only measure: it's what the engine can attest on its own. Real tokens live in the
    # caller's billing metadata — an LLM driver has no way to honestly report them.
    config = harness_config.current()
    if config.max_instruction_chars > 0 and cost_chars > config.max_instruction_chars:
        print(
            f"[harness] instruction char limit of {config.max_instruction_chars} "
            f"reached ({cost_chars}); stopping.",
            file=sys.stderr,
        )
        state_store.mark_terminal("budget")
        return "stop", trace.TraceOutcome.BUDGET

    # Typed error instead of silent "stop": the model receives the cause and can resend
    # the right command (corrective loop, not silent termination).
    if envelope is None:
        return _error_instruction("Could not parse the received JSON.", actions), trace.TraceOutcome.ERROR

    action = actions.get(envelope.value)
    if action is None:
        return _error_instruction(f"The command '{envelope.value}' does not exist.", actions), trace.TraceOutcome.ERROR

    # Contextual validation: the command exists, but does the VALUE meet the task's
    # expectation? Failed → same corrective-error path as the cases above; the driver
    # fixes and resends.
    if validators is not None:
        validator = validators.get(envelope.value)
        if validator is not None:
            rejected = validator(envelope)
            if not rejected.ok:
                return (
                    _error_instruction(
                        f"The command '{envelope.value}' was rejected: {rejected.reason} "
                        "Fix the 'args' content and resend the same command.",
                        actions,
                    ),
                    trace.TraceOutcome.ERROR,
                )

    # Time guard: a stuck task (infinite loop in domain logic) would hang the process
    # indefinitely. _run_with_timeout enforces the per-step ceiling; a timeout becomes a
    # typed error, caught here, following the same graceful path as the budget cut:
    # stderr diagnostic + "stop" on stdout (the channel the IDE client reads).
    try:
        result = _run_with_timeout(action, envelope, config.timeout_ms)
        return result, (trace.TraceOutcome.STOP if result == "stop" else trace.TraceOutcome.INSTRUCTION)
    except HarnessTimeoutError as ex:
        print(f"[harness] {ex}", file=sys.stderr)
        state_store.mark_terminal("timeout")
        return "stop", trace.TraceOutcome.TIMEOUT


# The task is a synchronous, OPAQUE function — it does not cooperate with cancellation.
# Python (CPython) cannot safely abort stuck synchronous code (there's no Thread.Abort),
# so the only real preemptive timeout is to run it on another thread and ABANDON whatever
# hangs. threading.Thread(daemon=True) — and not concurrent.futures.ThreadPoolExecutor —
# because since Python 3.9 the executor's workers are joined in an atexit handler, which
# would hang the process on exit if a task got stuck; a daemon thread is truly abandoned
# when the process exits, the same model as .NET's Task.Run + background threadpool.
def _run_with_timeout(action: Action, envelope: Envelope | None, timeout_ms: int) -> str:
    if timeout_ms <= 0:
        return action(envelope)  # guard disabled — no thread overhead

    result_box: list[str] = []
    error_box: list[BaseException] = []

    def runner() -> None:
        try:
            result_box.append(action(envelope))
        except BaseException as ex:  # noqa: BLE001 — re-raised on the main thread below
            error_box.append(ex)

    thread = threading.Thread(target=runner, daemon=True)
    thread.start()
    thread.join(timeout_ms / 1000)

    if thread.is_alive():
        raise HarnessTimeoutError(timeout_ms)

    if error_box:
        raise error_box[0]

    return result_box[0]


def _error_instruction(reason: str, actions: Mapping[str, Action]) -> str:
    valid = ", ".join(actions.keys())
    return (
        f"HARNESS PROTOCOL ERROR: {reason} Valid commands: {valid}. "
        "Review the 'value' field in your JSON response (reply with the JSON only, "
        "no code fences or commentary) and resend the command."
    )
