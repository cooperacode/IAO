using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Features.EditTask;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class EditTaskIntegrationTests
{
    [Fact]
    public async Task PutTask_updates_existing_task_title_in_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        var id = await InsertTaskAsync("Original title", isComplete: true);

        var response = await client.PutAsJsonAsync($"/tasks/{id}", new EditTaskRequest("  Updated title  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<EditTaskResponse>();
        Assert.NotNull(task);
        Assert.Equal(id, task.Id);
        Assert.Equal("completed", task.Status);
        Assert.Equal("Updated title", task.Title);
        Assert.Equal(("Updated title", true), await LoadTaskAsync(id));
    }

    [Fact]
    public async Task PutTask_rejects_empty_title_without_changing_existing_task()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        var id = await InsertTaskAsync("Keep title", isComplete: false);

        var response = await client.PutAsJsonAsync($"/tasks/{id}", new EditTaskRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(("Keep title", false), await LoadTaskAsync(id));
    }

    [Fact]
    public async Task PutTask_returns_not_found_for_missing_task()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.PutAsJsonAsync("/tasks/999", new EditTaskRequest("Updated"));

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

    private static async Task<(string Title, bool IsComplete)> LoadTaskAsync(long id)
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("""
            SELECT title, is_complete
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        return (reader.GetString(0), reader.GetBoolean(1));
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
