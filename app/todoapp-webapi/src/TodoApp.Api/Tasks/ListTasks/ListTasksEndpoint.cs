using Npgsql;

namespace TodoApp.Api.Tasks.ListTasks;

public static class ListTasksEndpoint
{
    public static void MapListTasks(this IEndpointRouteBuilder app)
    {
        app.MapGet("/tasks", ListTasksAsync);
    }

    private static async Task<IResult> ListTasksAsync(
        string? status,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var filter = ListTaskRules.ParseStatusFilter(status);
        if (!filter.IsValid)
        {
            return Results.BadRequest(new ListTasksErrorResponse(filter.Error!));
        }

        var commandText = filter.StatusFilter switch
        {
            ListTaskStatusFilter.Pending => """
                SELECT id, title, is_completed
                FROM tasks
                WHERE is_completed = FALSE
                ORDER BY id;
                """,
            ListTaskStatusFilter.Completed => """
                SELECT id, title, is_completed
                FROM tasks
                WHERE is_completed = TRUE
                ORDER BY id;
                """,
            _ => """
                SELECT id, title, is_completed
                FROM tasks
                ORDER BY id;
                """
        };

        await using var command = dataSource.CreateCommand(commandText);

        var tasks = new List<ListTaskResponse>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tasks.Add(new ListTaskResponse(
                reader.GetInt64(0),
                ListTaskRules.ToStatus(reader.GetBoolean(2)),
                reader.GetString(1)));
        }

        return Results.Ok(tasks);
    }
}

public static class ListTaskRules
{
    public static string ToStatus(bool isCompleted) =>
        isCompleted ? "completed" : "pending";

    public static ListTaskFilter ParseStatusFilter(string? status)
    {
        if (status is null)
        {
            return new ListTaskFilter(true, ListTaskStatusFilter.All, null);
        }

        return status.Trim().ToLowerInvariant() switch
        {
            "all" => new ListTaskFilter(true, ListTaskStatusFilter.All, null),
            "pending" => new ListTaskFilter(true, ListTaskStatusFilter.Pending, null),
            "completed" => new ListTaskFilter(true, ListTaskStatusFilter.Completed, null),
            _ => new ListTaskFilter(false, ListTaskStatusFilter.All, "Invalid status. Use pending, completed, or all.")
        };
    }
}

public sealed record ListTaskResponse(long Id, string Status, string Title);

public sealed record ListTaskFilter(bool IsValid, ListTaskStatusFilter StatusFilter, string? Error);

public enum ListTaskStatusFilter
{
    All,
    Pending,
    Completed
}

public sealed record ListTasksErrorResponse(string Error);
