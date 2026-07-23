using System.Diagnostics;
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

    private static AutomatedVerifyResult TryAutomatedVerify()
    {
        if (!int.TryParse(State("current_feature_id"), out var featureId))
            return AutomatedVerifyResult.Missing();

        var targetDir = ResolveTargetDir(RunConfigStore.Load().TargetDir);
        var script = Path.Combine(targetDir, "verify-feature.sh");
        if (!File.Exists(script))
            return AutomatedVerifyResult.Missing();

        var result = RunVerifyScript(targetDir, script, featureId);
        var logPath = WriteVerifyLog(targetDir, script, featureId, result);
        if (result.TimedOut)
        {
            return AutomatedVerifyResult.Failed(
                $"FAIL: verify-feature.sh {featureId} excedeu timeout ({VerifyTimeoutDescription()})"
                + VerifyOutputSuffix(result, logPath));
        }

        if (result.ExitCode == 0)
            return AutomatedVerifyResult.Passed(PassResult(featureId, result.Output, result.Error, logPath));

        return AutomatedVerifyResult.Failed(
            $"FAIL: verify-feature.sh {featureId} falhou (exit {result.ExitCode})"
            + VerifyOutputSuffix(result, logPath));
    }

    private static VerifyScriptResult RunVerifyScript(string targetDir, string script, int featureId)
    {
        using var process = new Process();
        process.StartInfo.FileName = "bash";
        process.StartInfo.WorkingDirectory = targetDir;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.ArgumentList.Add(script);
        process.StartInfo.ArgumentList.Add(featureId.ToString());

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
                // O processo pode ter terminado entre o WaitForExit e o Kill.
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
        return timeoutMs <= 0 ? "sem limite" : $"{timeoutMs}ms";
    }

    private static string WriteVerifyLog(
        string targetDir,
        string script,
        int featureId,
        VerifyScriptResult result)
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
                command: bash ./verify-feature.sh {featureId}
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
            return $"log indisponivel ({OneLine(ex.Message)})";
        }

        return displayPath;
    }

    private static string PassResult(int featureId, string output, string error, string logPath)
    {
        var firstLine = FirstMeaningfulLine(output, error);
        var result = firstLine.StartsWith("PASS", StringComparison.OrdinalIgnoreCase)
            ? Snippet(firstLine)
            : $"PASS: verify-feature.sh {featureId} passou";
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
        return text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "...";
    }

    private sealed record VerifyScriptResult(int ExitCode, string Output, string Error, bool TimedOut);
}
