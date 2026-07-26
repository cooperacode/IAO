using System.Text;
using Harness.Engine;

namespace Flows.Development;

public static partial class DevelopmentTasks
{
    private sealed record HandoffResult(bool Success, string Confirmation, string? Failure = null)
    {
        public static HandoffResult Ok(string confirmation) => new(true, confirmation);
        public static HandoffResult Failed(string failure) => new(false, "", failure);
    }

    private static string CompleteVerifiedFeature(string verifyResult)
    {
        var handoff = TryAutomatedHandoff(verifyResult);
        if (!handoff.Success)
        {
            Console.Error.WriteLine($"[dev] handoff automatico falhou: {handoff.Failure}");
            return HandoffPrompt(handoff.Failure);
        }

        Console.Error.WriteLine($"[dev] handoff automatico concluido: {handoff.Confirmation}");
        if (int.TryParse(State(CurrentFeatureIdKey), out var id))
            FeatureStore.MarkPassed(id);

        return FeatureStore.AllPassing() ? Done() : BearingsPrompt();
    }

    private static HandoffResult TryAutomatedHandoff(string verifyResult)
    {
        if (!int.TryParse(State(CurrentFeatureIdKey), out var featureId))
            return HandoffResult.Failed("feature atual ausente no state.json");

        var feature = FeatureStore.Load().FirstOrDefault(f => f.Id == featureId);
        var title = feature?.Title ?? State(CurrentFeatureTitleKey);
        if (string.IsNullOrWhiteSpace(title))
            title = $"feature #{featureId}";

        var config = RunConfigStore.Load();
        string targetDir;
        try
        {
            targetDir = ResolveTargetDir(config.TargetDir);
        }
        catch (InvalidOperationException ex)
        {
            return HandoffResult.Failed($"target_dir invalido: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            AppendProgress(targetDir, featureId, title, config.VerifyCmd, verifyResult);
        }
        catch (Exception ex)
        {
            return HandoffResult.Failed($"falha ao atualizar progress.txt: {ex.Message}");
        }

        var revParse = GitCommand.Run(targetDir, "rev-parse", "--show-toplevel");
        if (revParse.ExitCode != 0)
            return HandoffResult.Ok($"NO_GIT: {OneLine(revParse.Error, "diretorio-alvo fora de um repositorio Git")}");

        var add = GitCommand.Run(targetDir, "add", "-A", "--", ".", ":(exclude).harness");
        if (add.ExitCode != 0)
            return HandoffResult.Failed($"git add falhou: {OneLine(add.Error, add.Output)}");

        var diff = GitCommand.Run(targetDir, "diff", "--cached", "--quiet", "--", ".", ":(exclude).harness");
        if (diff.ExitCode == 0)
        {
            var head = GitCommand.Run(targetDir, "rev-parse", "--short", "HEAD");
            return head.ExitCode == 0
                ? HandoffResult.Ok(OneLine(head.Output, "NO_CHANGES"))
                : HandoffResult.Ok("NO_CHANGES");
        }
        if (diff.ExitCode > 1)
            return HandoffResult.Failed($"git diff --cached falhou: {OneLine(diff.Error, diff.Output)}");

        var commit = GitCommand.Run(
            targetDir, "commit", "-m", CommitMessage(featureId, title), "--", ".", ":(exclude).harness");
        if (commit.ExitCode != 0)
            return HandoffResult.Failed($"git commit falhou: {OneLine(commit.Error, commit.Output)}");

        var status = GitCommand.Run(targetDir, "status", "--short", "--", ".", ":(exclude).harness");
        if (status.ExitCode != 0)
            return HandoffResult.Failed($"git status falhou: {OneLine(status.Error, status.Output)}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            return HandoffResult.Failed($"diretorio-alvo ainda sujo apos commit: {OneLine(status.Output)}");

        var hash = GitCommand.Run(targetDir, "rev-parse", "--short", "HEAD");
        return hash.ExitCode == 0
            ? HandoffResult.Ok(OneLine(hash.Output, "COMMIT_CREATED"))
            : HandoffResult.Failed($"commit criado, mas hash nao foi lido: {OneLine(hash.Error, hash.Output)}");
    }

    /// <summary>
    /// Containment mínimo (RFC §6.3): recusa alvos que certamente não deveriam receber commits
    /// automáticos do agente — vazio, raiz do filesystem, HOME do usuário, ou o próprio
    /// diretório de instalação do harness (usando <see cref="AppContext.BaseDirectory"/> como
    /// proxy). Containment completo contra uma raiz de política assinada é trabalho de uma fase
    /// futura (capability broker); isto aqui é só a lista mínima de rejeição do RFC.
    /// </summary>
    private static string ResolveTargetDir(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new InvalidOperationException("target_dir vazio/whitespace nao e um diretorio-alvo valido.");

        var resolved = Path.GetFullPath(targetDir);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var root = Path.GetPathRoot(resolved);
        if (!string.IsNullOrEmpty(root) && string.Equals(resolved, root, comparison))
            throw new InvalidOperationException($"target_dir resolve para a raiz do sistema de arquivos ('{resolved}').");

        if (NormalizedOrNull(SafeSpecialFolder(Environment.SpecialFolder.UserProfile)) is { } home
            && string.Equals(Normalized(resolved), home, comparison))
            throw new InvalidOperationException($"target_dir resolve para o diretorio home do usuario ('{resolved}').");

        if (NormalizedOrNull(AppContext.BaseDirectory) is { } harnessBase
            && string.Equals(Normalized(resolved), harnessBase, comparison))
            throw new InvalidOperationException($"target_dir resolve para o diretorio de instalacao do harness ('{resolved}').");

        return resolved;
    }

    private static string? SafeSpecialFolder(Environment.SpecialFolder folder)
    {
        try
        {
            return Environment.GetFolderPath(folder);
        }
        catch
        {
            return null;
        }
    }

    private static string Normalized(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string? NormalizedOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Normalized(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private static void AppendProgress(
        string targetDir,
        int featureId,
        string title,
        string verifyCmd,
        string verifyResult)
    {
        var summary = OneLine(State(CurrentFeatureSummaryKey), "implementacao concluida");
        var verify = OneLine(verifyResult, "PASS");
        var command = string.IsNullOrWhiteSpace(verifyCmd) ? "comando de verificacao do projeto" : verifyCmd;
        var line =
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] Feature #{featureId} - {OneLine(title)}: "
            + $"{summary}. Verificar com: {OneLine(command)}. Resultado: {verify}";

        File.AppendAllText(Path.Combine(targetDir, "progress.txt"), line + Environment.NewLine);
    }

    private static string CommitMessage(int featureId, string title)
    {
        var suffix = OneLine(title);
        if (Encoding.UTF8.GetByteCount(suffix) > 72)
            suffix = TruncateUtf8Bytes(suffix, 72).TrimEnd();
        return $"feat(development): complete feature #{featureId} - {suffix}";
    }

    /// <summary>
    /// Corta <paramref name="text"/> em no máximo <paramref name="maxBytes"/> octetos UTF-8,
    /// recuando até uma fronteira de byte líder válida — nunca parte um caractere multibyte
    /// (acento, emoji) ao meio. Compartilhado por <see cref="CommitMessage"/> e
    /// <see cref="Snippet"/> (DevelopmentTasks.Verify.cs) — mesma partial class, sem
    /// dependência cruzada nova entre Harness.Engine e Flows.Development.
    /// </summary>
    private static string TruncateUtf8Bytes(string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= maxBytes)
            return text;

        var cut = maxBytes;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
            cut--;

        return Encoding.UTF8.GetString(bytes, 0, cut);
    }

    private static string OneLine(string? value, string fallback = "")
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized.Trim();
    }
}
