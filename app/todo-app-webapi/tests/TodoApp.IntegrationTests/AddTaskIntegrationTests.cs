using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Features.AddTask;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class AddTaskIntegrationTests
{
    [Fact]
    public async Task PostTasks_creates_pending_task_in_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.PostAsJsonAsync("/tasks", new AddTaskRequest("  Buy milk  "));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var task = await response.Content.ReadFromJsonAsync<AddTaskResponse>();
        Assert.NotNull(task);
        Assert.True(task.Id > 0);
        Assert.Equal("pending", task.Status);
        Assert.Equal("Buy milk", task.Title);

        var stored = await LoadTaskAsync(task.Id);

        Assert.Equal("Buy milk", stored.Title);
        Assert.False(stored.IsComplete);
    }

    [Fact]
    public async Task PostTasks_rejects_empty_title()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.PostAsJsonAsync("/tasks", new AddTaskRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountTasksAsync());
    }

    private static async Task ClearTasksAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountTasksAsync()
    {
        await using var dataSource = CreateDataSource();
        await using var command = dataSource.CreateCommand("SELECT COUNT(*) FROM tasks;");
        return Convert.ToInt32(await command.ExecuteScalarAsync());
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
