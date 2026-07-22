using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Features.CompleteTask;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class CompleteTaskIntegrationTests
{
    [Fact]
    public async Task PatchComplete_marks_existing_task_complete_in_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        var id = await InsertTaskAsync("Ship slice", isComplete: false);

        var response = await client.PatchAsync($"/tasks/{id}/complete", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<CompleteTaskResponse>();
        Assert.NotNull(task);
        Assert.Equal(id, task.Id);
        Assert.Equal("completed", task.Status);
        Assert.Equal("Ship slice", task.Title);
        Assert.True(await LoadCompletionStateAsync(id));
    }

    [Fact]
    public async Task PatchComplete_returns_not_found_for_missing_task()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.PatchAsync("/tasks/999/complete", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task ClearTasksAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertTaskAsync(string title, bool isComplete)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_complete)
            VALUES (@title, @isComplete)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("isComplete", isComplete);

        return (long)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Postgres did not return inserted task id."));
    }

    private static async Task<bool> LoadCompletionStateAsync(long id)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("""
            SELECT is_complete
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);

        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Inserted task was not found."));
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
