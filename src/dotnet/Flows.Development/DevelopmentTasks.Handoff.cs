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
        if (int.TryParse(State("current_feature_id"), out var id))
            FeatureStore.MarkPassed(id);

        return FeatureStore.AllPassing() ? Done() : BearingsPrompt();
    }

    private static HandoffResult TryAutomatedHandoff(string verifyResult)
    {
        if (!int.TryParse(State("current_feature_id"), out var featureId))
            return HandoffResult.Failed("feature atual ausente no state.json");

        var feature = FeatureStore.Load().FirstOrDefault(f => f.Id == featureId);
        var title = feature?.Title ?? State("current_feature_title");
        if (string.IsNullOrWhiteSpace(title))
            title = $"feature #{featureId}";

        var config = RunConfigStore.Load();
        var targetDir = ResolveTargetDir(config.TargetDir);

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

    private static string ResolveTargetDir(string targetDir)
    {
        var configured = string.IsNullOrWhiteSpace(targetDir) ? "." : targetDir;
        return Path.GetFullPath(configured);
    }

    private static void AppendProgress(
        string targetDir,
        int featureId,
        string title,
        string verifyCmd,
        string verifyResult)
    {
        var summary = OneLine(State("current_feature_summary"), "implementacao concluida");
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
        if (suffix.Length > 72)
            suffix = suffix[..72].TrimEnd();
        return $"feat(development): complete feature #{featureId} - {suffix}";
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
