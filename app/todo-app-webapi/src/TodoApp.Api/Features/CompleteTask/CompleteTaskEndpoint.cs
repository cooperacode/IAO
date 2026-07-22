using Npgsql;

namespace TodoApp.Api.Features.CompleteTask;

public static class CompleteTaskEndpoint
{
    public static IEndpointRouteBuilder MapCompleteTask(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPatch("/tasks/{id:long}/complete", async Task<IResult> (
            long id,
            NpgsqlDataSource dataSource,
            EmailNotifier emailNotifier,
            CancellationToken cancellationToken) =>
        {
            CompleteTaskRow? currentTask = null;
            await using (var selectCommand = dataSource.CreateCommand("""
                SELECT id, title, is_complete
                FROM tasks
                WHERE id = @id
                """))
            {
                selectCommand.Parameters.AddWithValue("id", id);

                await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    currentTask = new CompleteTaskRow(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetBoolean(2));
                }
            }

            if (currentTask is null)
            {
                return Results.NotFound(new { error = "Task not found." });
            }

            var previousStatus = CompleteTaskRules.ToStatus(currentTask.IsComplete);
            var nextCompletionState = CompleteTaskRules.ApplyTransition(currentTask.IsComplete);
            var nextStatus = CompleteTaskRules.ToStatus(nextCompletionState);

            if (currentTask.IsComplete != nextCompletionState)
            {
                await using var updateCommand = dataSource.CreateCommand("""
                    UPDATE tasks
                    SET is_complete = @isComplete
                    WHERE id = @id;
                    """);
                updateCommand.Parameters.AddWithValue("id", id);
                updateCommand.Parameters.AddWithValue("isComplete", nextCompletionState);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);

                await emailNotifier.NotifyTaskStatusChangedAsync(
                    new TaskStatusChangedNotification(currentTask.Id, currentTask.Title, previousStatus, nextStatus),
                    cancellationToken);
            }

            var response = new CompleteTaskResponse(
                currentTask.Id,
                nextStatus,
                currentTask.Title);

            return Results.Ok(response);
        });

        return endpoints;
    }
}

public sealed record CompleteTaskResponse(long Id, string Status, string Title);

internal sealed record CompleteTaskRow(long Id, string Title, bool IsComplete);
