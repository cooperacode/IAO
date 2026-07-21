using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class EditTaskEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource _dataSource;

    public EditTaskEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TODO_DB_CONNECTION"] = ConnectionString
                    });
                });
            });

        _dataSource = NpgsqlDataSource.Create(ConnectionString);
    }

    [Fact]
    public async Task PutTask_updates_title_in_postgres()
    {
        var taskId = await SeedTaskAsync("Old title", isCompleted: true);
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/tasks/{taskId}", new EditTaskRequest("  New title  "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var edited = await response.Content.ReadFromJsonAsync<EditTaskResponse>();
        Assert.NotNull(edited);
        Assert.Equal(taskId, edited.Id);
        Assert.Equal("completed", edited.Status);
        Assert.Equal("New title", edited.Title);

        await using var command = _dataSource.CreateCommand("""
            SELECT title, is_completed
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", taskId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("New title", reader.GetString(0));
        Assert.True(reader.GetBoolean(1));
        Assert.False(await reader.ReadAsync());
    }

    [Fact]
    public async Task PutTask_rejects_empty_title_without_changing_existing_task()
    {
        var taskId = await SeedTaskAsync("Original title", isCompleted: false);
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync($"/tasks/{taskId}", new EditTaskRequest("   "));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EditTaskErrorResponse>();
        Assert.Equal("Title is required.", error?.Error);
        Assert.Equal("Original title", await ReadTitleAsync(taskId));
    }

    [Fact]
    public async Task PutTask_returns_not_found_for_missing_task()
    {
        using var client = _factory.CreateClient();

        var response = await client.PutAsJsonAsync("/tasks/999", new EditTaskRequest("New title"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<EditTaskErrorResponse>();
        Assert.Equal("Task not found.", error?.Error);
    }

    public async Task InitializeAsync()
    {
        await using var command = _dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _dataSource.DisposeAsync();
    }

    private async Task<long> SeedTaskAsync(string title, bool isCompleted)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_completed)
            VALUES (@title, @isCompleted)
            RETURNING id;
            """);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("isCompleted", isCompleted);
        return (long)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Task seed failed."));
    }

    private async Task<string> ReadTitleAsync(long taskId)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT title
            FROM tasks
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", taskId);
        return (string)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Task not found."));
    }

    private sealed record EditTaskRequest(string? Title);

    private sealed record EditTaskResponse(long Id, string Status, string Title);

    private sealed record EditTaskErrorResponse(string Error);
}
