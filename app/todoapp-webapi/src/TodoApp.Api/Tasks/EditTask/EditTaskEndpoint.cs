using Npgsql;

namespace TodoApp.Api.Tasks.EditTask;

public static class EditTaskEndpoint
{
    public static void MapEditTask(this IEndpointRouteBuilder app)
    {
        app.MapPut("/tasks/{id:long}", EditTaskAsync);
    }

    private static async Task<IResult> EditTaskAsync(
        long id,
        EditTaskRequest request,
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        var validation = EditTaskRules.ValidateTitle(request.Title);
        if (!validation.IsValid)
        {
            return Results.BadRequest(new EditTaskErrorResponse(validation.Error!));
        }

        await using var command = dataSource.CreateCommand("""
            UPDATE tasks
            SET title = @title
            WHERE id = @id
            RETURNING id, title, is_completed;
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("title", validation.Title!);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return Results.NotFound(new EditTaskErrorResponse("Task not found."));
        }

        return Results.Ok(new EditTaskResponse(
            reader.GetInt64(0),
            EditTaskRules.ToStatus(reader.GetBoolean(2)),
            reader.GetString(1)));
    }
}

public static class EditTaskRules
{
    public static EditTaskValidation ValidateTitle(string? title)
    {
        var normalizedTitle = title?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return new EditTaskValidation(false, null, "Title is required.");
        }

        return new EditTaskValidation(true, normalizedTitle, null);
    }

    public static string ToStatus(bool isCompleted) =>
        isCompleted ? "completed" : "pending";
}

public sealed record EditTaskRequest(string? Title);

public sealed record EditTaskResponse(long Id, string Status, string Title);

public sealed record EditTaskValidation(bool IsValid, string? Title, string? Error);

public sealed record EditTaskErrorResponse(string Error);
