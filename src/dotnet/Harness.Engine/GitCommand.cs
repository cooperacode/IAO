using System.Diagnostics;

namespace Harness.Engine;

/// <summary>
/// Small, shell-safe runner for Git commands. The engine provides the mechanism; flows
/// decide which commands to run and how to interpret the result.
/// </summary>
public static class GitCommand
{
    // Stable, empty directory used as core.hooksPath — neutralizes any local or global
    // hook from the target repo (RFC §6.11). Created once, idempotent; never receives scripts.
    private static readonly string NoHooksDir = EnsureNoHooksDir();

    private static string EnsureNoHooksDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "iao-no-hooks");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static GitCommandResult Run(string workingDirectory, params string[] args)
    {
        using var process = new Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.UseShellExecute = false;

        // Git isolation (RFC §6.11): always ahead of the caller's args. Neutralizes hooks
        // (core.hooksPath to an empty directory), the credential helper (avoids a prompt or
        // a stored-credential leak), and the pager (core.pager=cat avoids hanging in an
        // interactive subprocess waiting for stdin that never arrives).
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add($"core.hooksPath={NoHooksDir}");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("credential.helper=");
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("core.pager=cat");

        foreach (var arg in args)
            process.StartInfo.ArgumentList.Add(arg);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new GitCommandResult(-1, "", ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }
}

public sealed record GitCommandResult(int ExitCode, string Output, string Error);
