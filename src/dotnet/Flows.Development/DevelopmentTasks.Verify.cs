using System.Diagnostics;
using System.Text;
using Harness.Engine;

namespace Flows.Development;

public static partial class DevelopmentTasks
{
    private sealed record AutomatedVerifyResult(bool Attempted, bool Success, string Result)
    {
        public static AutomatedVerifyResult Missing() => new(false, false, "");
        public static AutomatedVerifyResult Passed(string result) => new(true, true, result);
        public static AutomatedVerifyResult Failed(string result) => new(true, false, result);
    }

    private sealed record AutomatedSmokeResult(bool Success, string Result);

    private static AutomatedVerifyResult TryDeterministicVerify()
    {
        var scripted = TryAutomatedVerify();
        return scripted.Attempted ? scripted : TryConfiguredVerify();
    }

    private static AutomatedVerifyResult TryAutomatedVerify()
    {
        if (!int.TryParse(State(CurrentFeatureIdKey), out var featureId))
            return AutomatedVerifyResult.Missing();

        string targetDir;
        try
        {
            targetDir = ResolveTargetDir(RunConfigStore.Load().TargetDir);
        }
        catch (InvalidOperationException ex)
        {
            // Invalid target_dir (root, home, harness install) -> same "automatic
            // verification not attempted" path as a target_dir with no
            // verify-feature.sh; doesn't bring down the process with an unhandled
            // exception.
            HarnessLog.Error($"[dev] invalid target_dir for automatic verify: {ex.Message}");
            return AutomatedVerifyResult.Missing();
        }

        var script = Path.Combine(targetDir, "verify-feature.sh");
        if (!File.Exists(script))
            return AutomatedVerifyResult.Missing();

        var result = RunScript(targetDir, script, featureId.ToString());
        var logPath = WriteVerifyLog(targetDir, script, featureId, result);
        if (result.TimedOut)
        {
            return AutomatedVerifyResult.Failed(
                $"FAIL: verify-feature.sh {featureId} exceeded timeout ({VerifyTimeoutDescription()})"
                + VerifyOutputSuffix(result, logPath));
        }

        if (result.ExitCode == 0)
            return AutomatedVerifyResult.Passed(PassResult(featureId, result.Output, result.Error, logPath));

        return AutomatedVerifyResult.Failed(
            $"FAIL: verify-feature.sh {featureId} failed (exit {result.ExitCode})"
            + VerifyOutputSuffix(result, logPath));
    }

    private static AutomatedVerifyResult TryConfiguredVerify()
    {
        string targetDir;
        try
        {
            targetDir = ResolveTargetDir(RunConfigStore.Load().TargetDir);
        }
        catch (InvalidOperationException ex)
        {
            return AutomatedVerifyResult.Failed($"FAIL: invalid target directory: {ex.Message}");
        }

        var command = RunConfigStore.Load().VerifyCmd.Trim();
        var tokens = TokenizeCommand(command);
        if (tokens.Count == 0)
            return AutomatedVerifyResult.Failed("FAIL: no deterministic verify command is configured");

        var result = RunCommand(targetDir, tokens);
        var featureId = State(CurrentFeatureIdKey);
        var logPath = WriteVerifyLog(
            targetDir,
            string.Join(" ", tokens),
            int.TryParse(featureId, out var id) ? id : 0,
            result,
            command);

        if (result.TimedOut)
            return AutomatedVerifyResult.Failed(
                $"FAIL: verify command exceeded timeout ({VerifyTimeoutDescription()})"
                + VerifyOutputSuffix(result, logPath));

        if (result.ExitCode == 0)
            return AutomatedVerifyResult.Passed(
                $"PASS: verify command passed{LogSuffix(logPath)}");

        return AutomatedVerifyResult.Failed(
            $"FAIL: verify command failed (exit {result.ExitCode})"
            + VerifyOutputSuffix(result, logPath));
    }

    private static AutomatedSmokeResult TryAutomatedSmoke()
    {
        string targetDir;
        try
        {
            targetDir = ResolveTargetDir(RunConfigStore.Load().TargetDir);
        }
        catch (InvalidOperationException ex)
        {
            return new(false, $"FAIL: invalid target directory: {ex.Message}");
        }

        var script = Path.Combine(targetDir, "init.sh");
        if (!File.Exists(script))
            return new(false, $"FAIL: init.sh was not found in {targetDir}");

        var result = RunScript(targetDir, script);
        var logPath = WriteSmokeLog(targetDir, script, result);
        if (result.TimedOut)
            return new(false, $"FAIL: init.sh exceeded timeout ({VerifyTimeoutDescription()}). Log: {logPath}");

        if (result.ExitCode != 0)
            return new(false, $"FAIL: init.sh failed (exit {result.ExitCode}). Log: {logPath}");

        return new(true, $"PASS: init.sh completed. Log: {logPath}");
    }

    private static VerifyScriptResult RunScript(string targetDir, string script, params string[] args) =>
        RunProcess(targetDir, "bash", [script, .. args]);

    private static VerifyScriptResult RunCommand(string targetDir, IReadOnlyList<string> tokens) =>
        RunProcess(targetDir, tokens[0], tokens.Skip(1).ToArray());

    private static VerifyScriptResult RunProcess(
        string targetDir, string fileName, IReadOnlyList<string> args)
    {
        using var process = new Process();
        process.StartInfo.FileName = fileName;
        process.StartInfo.WorkingDirectory = targetDir;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new VerifyScriptResult(-1, "", ex.Message, false);
        }

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var timeoutMs = VerifyTimeoutMs();
        var completed = timeoutMs <= 0
            ? WaitIndefinitely(process)
            : process.WaitForExit(timeoutMs);

        if (!completed)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process may have ended between WaitForExit and Kill.
            }
            process.WaitForExit();
            return new VerifyScriptResult(
                process.ExitCode,
                stdout.GetAwaiter().GetResult(),
                stderr.GetAwaiter().GetResult(),
                true);
        }

        process.WaitForExit();
        return new VerifyScriptResult(
            process.ExitCode,
            stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult(),
            false);
    }

    private static IReadOnlyList<string> TokenizeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return [];

        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var escaped = false;

        foreach (var character in command.Trim())
        {
            if (escaped)
            {
                current.Append(character);
                escaped = false;
                continue;
            }

            if (character == '\\' && quote != '\'')
            {
                escaped = true;
                continue;
            }

            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
                else
                    current.Append(character);
                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character is ';' or '&' or '|' or '<' or '>' or '`' or '$')
                return [];

            if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (escaped || quote != '\0')
            return [];

        if (current.Length > 0)
            tokens.Add(current.ToString());

        // The harness executes an argv vector, not a shell program. Explicit shell
        // evaluation would reintroduce the very delegation this path is meant to remove.
        if (tokens.Count >= 2
            && tokens[0] is "bash" or "sh" or "zsh" or "fish" or "pwsh" or "powershell" or "cmd"
            && tokens.Skip(1).Any(token => token is "-c" or "-Command" or "/c"))
        {
            return [];
        }

        return tokens;
    }

    private static bool WaitIndefinitely(Process process)
    {
        process.WaitForExit();
        return true;
    }

    private static int VerifyTimeoutMs()
    {
        var timeoutMs = HarnessConfig.Current.TimeoutMs;
        if (timeoutMs <= 0)
            return 0;

        var margin = Math.Min(500, Math.Max(1, timeoutMs / 10));
        return Math.Max(1, timeoutMs - margin);
    }

    private static string VerifyTimeoutDescription()
    {
        var timeoutMs = VerifyTimeoutMs();
        return timeoutMs <= 0 ? "no limit" : $"{timeoutMs}ms";
    }

    private static string WriteVerifyLog(
        string targetDir,
        string script,
        int featureId,
        VerifyScriptResult result,
        string? command = null)
    {
        const string relativeDir = ".harness/logs";
        var relativePath = Path.Combine(relativeDir, $"verify-feature-{featureId}.log");
        var displayPath = relativePath.Replace('\\', '/');

        try
        {
            var fullPath = Path.GetFullPath(relativePath, Directory.GetCurrentDirectory());
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, $"""
                timestampUtc: {DateTime.UtcNow:O}
                command: {command ?? $"bash ./verify-feature.sh {featureId}"}
                cwd: {targetDir}
                script: {script}
                exitCode: {result.ExitCode}
                timedOut: {result.TimedOut}

                --- stdout ---
                {result.Output}

                --- stderr ---
                {result.Error}
                """);
        }
        catch (Exception ex)
        {
            return $"log unavailable ({OneLine(ex.Message)})";
        }

        return displayPath;
    }

    private static string WriteSmokeLog(string targetDir, string script, VerifyScriptResult result)
    {
        const string relativePath = ".harness/logs/smoke.log";
        try
        {
            var fullPath = Path.GetFullPath(relativePath, Directory.GetCurrentDirectory());
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, $"""
                timestampUtc: {DateTime.UtcNow:O}
                command: bash {script}
                cwd: {targetDir}
                exitCode: {result.ExitCode}
                timedOut: {result.TimedOut}

                --- stdout ---
                {result.Output}

                --- stderr ---
                {result.Error}
                """);
        }
        catch (Exception ex)
        {
            return $"log unavailable ({OneLine(ex.Message)})";
        }

        return relativePath;
    }

    private static string PassResult(int featureId, string output, string error, string logPath)
    {
        var firstLine = FirstMeaningfulLine(output, error);
        var result = firstLine.StartsWith("PASS", StringComparison.OrdinalIgnoreCase)
            ? Snippet(firstLine)
            : $"PASS: verify-feature.sh {featureId} passed";
        return result + LogSuffix(logPath);
    }

    private static string VerifyOutputSuffix(VerifyScriptResult result, string logPath)
    {
        var output = Snippet(FirstMeaningfulLine(result.Output, result.Error));
        return string.IsNullOrWhiteSpace(output)
            ? LogSuffix(logPath)
            : $": {output}{LogSuffix(logPath)}";
    }

    private static string FirstMeaningfulLine(params string?[] values)
    {
        foreach (var value in values)
        {
            var lines = (value ?? string.Empty)
                .Replace('\r', '\n')
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    return line;
            }
        }

        return "";
    }

    private static string LogSuffix(string logPath) =>
        string.IsNullOrWhiteSpace(logPath) ? "" : $". Log: {logPath}";

    private static string Snippet(string value, int maxChars = 240)
    {
        var text = OneLine(value);
        return Encoding.UTF8.GetByteCount(text) <= maxChars
            ? text
            : TruncateUtf8Bytes(text, maxChars).TrimEnd() + "...";
    }

    private sealed record VerifyScriptResult(int ExitCode, string Output, string Error, bool TimedOut);
}
