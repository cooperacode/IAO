namespace TodoApp.Api.Features.CompleteTask;

public static class CompleteTaskRules
{
    public static bool ApplyTransition(bool currentlyComplete)
    {
        return true;
    }

    public static string ToStatus(bool isComplete)
    {
        return isComplete ? "completed" : "pending";
    }
}
