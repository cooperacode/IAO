namespace Harness.Engine;

/// <summary>
/// A step's execution timeout was exceeded (see <see cref="HarnessConfig.TimeoutMs"/>).
/// Thrown inside <see cref="TaskRegistry"/> and caught right there: it becomes a stderr
/// diagnostic + <c>"stop"</c> on stdout — the same graceful-termination contract as the
/// other guards (step and cost ceilings).
/// </summary>
public sealed class HarnessTimeoutException(int timeoutMs)
    : Exception($"task execution exceeded the {timeoutMs}ms timeout; stopping.");
