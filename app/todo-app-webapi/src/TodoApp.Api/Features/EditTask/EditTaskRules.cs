namespace TodoApp.Api.Features.EditTask;

public static class EditTaskRules
{
    public static bool TryNormalizeTitle(string? title, out string normalizedTitle)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            normalizedTitle = string.Empty;
            return false;
        }

        normalizedTitle = title.Trim();
        return true;
    }

    public static string ToStatus(bool isComplete)
    {
        return isComplete ? "completed" : "pending";
    }
}
