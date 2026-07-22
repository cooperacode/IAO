namespace TodoApp.Api.Features.AddTask;

public static class AddTaskRules
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
}
