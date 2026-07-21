using Npgsql;

namespace TodoApp.Api.Tasks.RemoveTask;

public static class RemoveTaskEndpoint
{
    public static void MapRemoveTask(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/tasks/{id:long}", RemoveTaskAsync);
    }

    private static async Task<IResult> RemoveTaskAsync(
        long id,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            DELETE FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);

        var rowsAffected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (!RemoveTaskRules.WasRemoved(rowsAffected))
        {
            return Results.NotFound(new RemoveTaskErrorResponse("Task not found."));
        }

        return Results.NoContent();
    }
}

public static class RemoveTaskRules
{
    public static bool WasRemoved(int rowsAffected) =>
        rowsAffected > 0;
}

public sealed record RemoveTaskErrorResponse(string Error);
