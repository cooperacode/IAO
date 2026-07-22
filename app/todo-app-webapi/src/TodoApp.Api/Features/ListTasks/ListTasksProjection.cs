namespace TodoApp.Api.Features.ListTasks;

public static class ListTasksProjection
{
    public static bool TryParseStatusFilter(string? status, out TaskStatusFilter statusFilter)
    {
        if (string.IsNullOrWhiteSpace(status) ||
            string.Equals(status, "all", StringComparison.OrdinalIgnoreCase))
        {
            statusFilter = TaskStatusFilter.All;
            return true;
        }

        if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            statusFilter = TaskStatusFilter.Pending;
            return true;
        }

        if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        {
            statusFilter = TaskStatusFilter.Completed;
            return true;
        }

        statusFilter = TaskStatusFilter.All;
        return false;
    }

    public static ListTaskResponse ToResponse(long id, string title, bool isComplete)
    {
        return new ListTaskResponse(id, isComplete ? "completed" : "pending", title);
    }
}

public enum TaskStatusFilter
{
    All,
    Pending,
    Completed
}
