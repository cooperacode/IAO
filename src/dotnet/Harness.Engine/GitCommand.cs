using System.Diagnostics;

namespace Harness.Engine;

/// <summary>
/// Runner pequeno e shell-safe para comandos Git. A engine fornece o mecanismo; flows decidem
/// quais comandos rodar e como interpretar o resultado.
/// </summary>
public static class GitCommand
{
    public static GitCommandResult Run(string workingDirectory, params string[] args)
    {
        using var process = new Process();
        process.StartInfo.FileName = "git";
        process.StartInfo.WorkingDirectory = workingDirectory;
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
            return new GitCommandResult(-1, "", ex.Message);
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new GitCommandResult(process.ExitCode, stdout, stderr);
    }
}

public sealed record GitCommandResult(int ExitCode, string Output, string Error);
