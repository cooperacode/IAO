namespace TodoApp.Api.Features.RemoveTask;

public static class RemoveTaskRules
{
    public static bool WasRemoved(int rowsDeleted)
    {
        return rowsDeleted > 0;
    }
}
