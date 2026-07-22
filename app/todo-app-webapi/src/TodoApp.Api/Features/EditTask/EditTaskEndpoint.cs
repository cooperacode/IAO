using Npgsql;

namespace TodoApp.Api.Features.EditTask;

public static class EditTaskEndpoint
{
    public static IEndpointRouteBuilder MapEditTask(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPut("/tasks/{id:long}", async Task<IResult> (
            long id,
            EditTaskRequest request,
            NpgsqlDataSource dataSource,
            CancellationToken cancellationToken) =>
        {
            if (!EditTaskRules.TryNormalizeTitle(request.Title, out var title))
            {
                return Results.BadRequest(new { error = "Title is required." });
            }

            await using var command = dataSource.CreateCommand("""
                UPDATE tasks
                SET title = @title
                WHERE id = @id
                RETURNING id, title, is_complete;
                """);
            command.Parameters.AddWithValue("id", id);
            command.Parameters.AddWithValue("title", title);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.NotFound(new { error = "Task not found." });
            }

            var response = new EditTaskResponse(
                reader.GetInt64(0),
                EditTaskRules.ToStatus(reader.GetBoolean(2)),
                reader.GetString(1));

            return Results.Ok(response);
        });

        return endpoints;
    }
}

public sealed record EditTaskRequest(string? Title);

public sealed record EditTaskResponse(long Id, string Status, string Title);
