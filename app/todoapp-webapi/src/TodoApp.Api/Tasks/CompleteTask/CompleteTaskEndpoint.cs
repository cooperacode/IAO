using Npgsql;

namespace TodoApp.Api.Tasks.CompleteTask;

public static class CompleteTaskEndpoint
{
    public static void MapCompleteTask(this IEndpointRouteBuilder app)
    {
        app.MapPatch("/tasks/{id:long}/complete", CompleteTaskAsync);
    }

    private static async Task<IResult> CompleteTaskAsync(
        long id,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            UPDATE tasks
            SET is_completed = TRUE,
                completed_at = COALESCE(completed_at, NOW())
            WHERE id = @id
            RETURNING id, title, is_completed;
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.NotFound(new CompleteTaskErrorResponse("Task not found."));
        }

        return Results.Ok(new CompleteTaskResponse(
            reader.GetInt64(0),
            CompleteTaskRules.ToStatus(reader.GetBoolean(2)),
            reader.GetString(1)));
    }
}

public static class CompleteTaskRules
{
    public static string ToStatus(bool isCompleted) =>
        isCompleted ? "completed" : "pending";
}

public sealed record CompleteTaskResponse(long Id, string Status, string Title);

public sealed record CompleteTaskErrorResponse(string Error);
