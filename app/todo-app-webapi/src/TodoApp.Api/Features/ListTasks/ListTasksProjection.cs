namespace TodoApp.Api.Features.ListTasks;

public static class ListTasksProjection
{
    public static ListTaskResponse ToResponse(long id, string title, bool isComplete)
    {
        return new ListTaskResponse(id, isComplete ? "completed" : "pending", title);
    }
}
