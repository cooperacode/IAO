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
            Console.Error.WriteLine($"[dev] automatic handoff failed: {handoff.Failure}");
            return HandoffPrompt(handoff.Failure);
        }

        Console.Error.WriteLine($"[dev] automatic handoff completed: {handoff.Confirmation}");
        if (int.TryParse(State(CurrentFeatureIdKey), out var id))
            FeatureStore.MarkPassed(id);

        return FeatureStore.AllPassing() ? Done() : BearingsPrompt();
    }

    private static HandoffResult TryAutomatedHandoff(string verifyResult)
    {
        if (!int.TryParse(State(CurrentFeatureIdKey), out var featureId))
            return HandoffResult.Failed("current feature missing from state.json");

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
            return HandoffResult.Failed($"invalid target directory: {ex.Message}");
        }

        try
        {
            Directory.CreateDirectory(targetDir);
            AppendProgress(targetDir, featureId, title, config.VerifyCmd, verifyResult);
        }
        catch (Exception ex)
        {
            return HandoffResult.Failed($"failed to update progress.txt: {ex.Message}");
        }

        var revParse = GitCommand.Run(targetDir, "rev-parse", "--show-toplevel");
        if (revParse.ExitCode != 0)
            return HandoffResult.Ok($"NO_GIT: {OneLine(revParse.Error, "target directory is outside a Git repository")}");

        var add = GitCommand.Run(targetDir, "add", "-A", "--", ".", ":(exclude).harness");
        if (add.ExitCode != 0)
            return HandoffResult.Failed($"git add failed: {OneLine(add.Error, add.Output)}");

        var diff = GitCommand.Run(targetDir, "diff", "--cached", "--quiet", "--", ".", ":(exclude).harness");
        if (diff.ExitCode == 0)
        {
            var head = GitCommand.Run(targetDir, "rev-parse", "--short", "HEAD");
            return head.ExitCode == 0
                ? HandoffResult.Ok(OneLine(head.Output, "NO_CHANGES"))
                : HandoffResult.Ok("NO_CHANGES");
        }
        if (diff.ExitCode > 1)
            return HandoffResult.Failed($"git diff --cached failed: {OneLine(diff.Error, diff.Output)}");

        var commit = GitCommand.Run(
            targetDir, "commit", "-m", CommitMessage(featureId, title), "--", ".", ":(exclude).harness");
        if (commit.ExitCode != 0)
            return HandoffResult.Failed($"git commit failed: {OneLine(commit.Error, commit.Output)}");

        var status = GitCommand.Run(targetDir, "status", "--short", "--", ".", ":(exclude).harness");
        if (status.ExitCode != 0)
            return HandoffResult.Failed($"git status failed: {OneLine(status.Error, status.Output)}");
        if (!string.IsNullOrWhiteSpace(status.Output))
            return HandoffResult.Failed($"target directory still dirty after commit: {OneLine(status.Output)}");

        var hash = GitCommand.Run(targetDir, "rev-parse", "--short", "HEAD");
        return hash.ExitCode == 0
            ? HandoffResult.Ok(OneLine(hash.Output, "COMMIT_CREATED"))
            : HandoffResult.Failed($"commit created, but the hash could not be read: {OneLine(hash.Error, hash.Output)}");
    }

    /// <summary>
    /// Minimal containment (RFC §6.3): rejects targets that should certainly never
    /// receive automatic commits from the agent — empty, filesystem root, the user's
    /// HOME, or the harness's own install directory (using <see cref="AppContext.BaseDirectory"/>
    /// as a proxy). Full containment against a signed policy root is future-phase work
    /// (capability broker); this is just the RFC's minimal rejection list.
    /// </summary>
    private static string ResolveTargetDir(string targetDir)
    {
        if (string.IsNullOrWhiteSpace(targetDir))
            throw new InvalidOperationException("target_dir empty/whitespace is not a valid target directory.");

        var resolved = Path.GetFullPath(targetDir);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var root = Path.GetPathRoot(resolved);
        if (!string.IsNullOrEmpty(root) && string.Equals(resolved, root, comparison))
            throw new InvalidOperationException($"target_dir resolves to the filesystem root ('{resolved}').");

        if (NormalizedOrNull(SafeSpecialFolder(Environment.SpecialFolder.UserProfile)) is { } home
            && string.Equals(Normalized(resolved), home, comparison))
            throw new InvalidOperationException($"target_dir resolves to the user's home directory ('{resolved}').");

        if (NormalizedOrNull(AppContext.BaseDirectory) is { } harnessBase
            && string.Equals(Normalized(resolved), harnessBase, comparison))
            throw new InvalidOperationException($"target_dir resolves to the harness install directory ('{resolved}').");

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
        var summary = OneLine(State(CurrentFeatureSummaryKey), "implementation completed");
        var verify = OneLine(verifyResult, "PASS");
        var command = string.IsNullOrWhiteSpace(verifyCmd) ? "the project's verify command" : verifyCmd;
        var line =
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC] Feature #{featureId} - {OneLine(title)}: "
            + $"{summary}. Verify with: {OneLine(command)}. Result: {verify}";

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
    /// Cuts <paramref name="text"/> at no more than <paramref name="maxBytes"/> UTF-8
    /// octets, backing off to a valid leading-byte boundary — never splits a multi-byte
    /// character (accent, emoji) in half. Shared by <see cref="CommitMessage"/> and
    /// <see cref="Snippet"/> (DevelopmentTasks.Verify.cs) — same partial class, no new
    /// cross-dependency between Harness.Engine and Flows.Development.
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
