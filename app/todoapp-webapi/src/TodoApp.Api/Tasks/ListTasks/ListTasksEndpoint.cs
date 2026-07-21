using Npgsql;

namespace TodoApp.Api.Tasks.ListTasks;

public static class ListTasksEndpoint
{
    public static void MapListTasks(this IEndpointRouteBuilder app)
    {
        app.MapGet("/tasks", ListTasksAsync);
    }

    private static async Task<IResult> ListTasksAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, title, is_completed
            FROM tasks
            ORDER BY id;
            """);

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
}

public sealed record ListTaskResponse(long Id, string Status, string Title);
