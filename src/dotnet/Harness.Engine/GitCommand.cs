using System.Diagnostics;

namespace Harness.Engine;

/// <summary>
/// Runner pequeno e shell-safe para comandos Git. A engine fornece o mecanismo; flows decidem
/// quais comandos rodar e como interpretar o resultado.
/// </summary>
public static class GitCommand
{
    // Diretório vazio e estável usado como core.hooksPath — neutraliza qualquer hook local ou
    // global do repo-alvo (RFC §6.11). Criado uma vez, idempotente; nunca recebe scripts.
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

        // Isolamento de Git (RFC §6.11): à frente dos args do chamador, sempre. Neutraliza
        // hooks (core.hooksPath para um diretório vazio), credential helper (evita prompt ou
        // vazamento de credencial armazenada) e pager (core.pager=cat evita travar num
        // subprocesso interativo esperando stdin que nunca chega).
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
