using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace TodoApp.IntegrationTests;

public sealed class ListTasksEndpointTests : IAsyncLifetime
{
    private const string ConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly NpgsqlDataSource _dataSource;

    public ListTasksEndpointTests()
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
    public async Task GetTasks_returns_all_tasks_from_postgres_ordered_by_id()
    {
        await SeedTaskAsync("Pending task", isCompleted: false);
        await SeedTaskAsync("Completed task", isCompleted: true);
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<List<ListTaskResponse>>();
        Assert.NotNull(tasks);
        Assert.Collection(
            tasks,
            first =>
            {
                Assert.Equal(1, first.Id);
                Assert.Equal("pending", first.Status);
                Assert.Equal("Pending task", first.Title);
            },
            second =>
            {
                Assert.Equal(2, second.Id);
                Assert.Equal("completed", second.Status);
                Assert.Equal("Completed task", second.Title);
            });
    }

    [Fact]
    public async Task GetTasks_returns_empty_array_when_there_are_no_tasks()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<List<ListTaskResponse>>();
        Assert.NotNull(tasks);
        Assert.Empty(tasks);
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

    private async Task SeedTaskAsync(string title, bool isCompleted)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO tasks (title, is_completed)
            VALUES (@title, @isCompleted);
            """);
        command.Parameters.AddWithValue("title", title);
        command.Parameters.AddWithValue("isCompleted", isCompleted);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record ListTaskResponse(long Id, string Status, string Title);
}
