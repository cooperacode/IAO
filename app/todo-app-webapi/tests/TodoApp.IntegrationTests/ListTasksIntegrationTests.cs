using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Features.ListTasks;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class ListTasksIntegrationTests
{
    [Fact]
    public async Task GetTasks_lists_all_persisted_tasks_ordered_by_id()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        var firstId = await InsertTaskAsync("Buy milk", isComplete: false);
        var secondId = await InsertTaskAsync("Read docs", isComplete: true);

        var response = await client.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<ListTaskResponse[]>();
        Assert.NotNull(tasks);
        Assert.Collection(
            tasks,
            first =>
            {
                Assert.Equal(firstId, first.Id);
                Assert.Equal("pending", first.Status);
                Assert.Equal("Buy milk", first.Title);
            },
            second =>
            {
                Assert.Equal(secondId, second.Id);
                Assert.Equal("completed", second.Status);
                Assert.Equal("Read docs", second.Title);
            });
    }

    [Fact]
    public async Task GetTasks_returns_empty_list_when_no_tasks_exist()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();

        var response = await client.GetAsync("/tasks");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<ListTaskResponse[]>();
        Assert.NotNull(tasks);
        Assert.Empty(tasks);
    }

    [Theory]
    [InlineData("pending", "Buy milk")]
    [InlineData("completed", "Read docs")]
    public async Task GetTasks_filters_by_status(string status, string expectedTitle)
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        await InsertTaskAsync("Buy milk", isComplete: false);
        await InsertTaskAsync("Read docs", isComplete: true);

        var response = await client.GetAsync($"/tasks?status={status}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<ListTaskResponse[]>();
        Assert.NotNull(tasks);
        var task = Assert.Single(tasks);
        Assert.Equal(status, task.Status);
        Assert.Equal(expectedTitle, task.Title);
    }

    [Fact]
    public async Task GetTasks_accepts_all_status_filter()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        await ClearTasksAsync();
        await InsertTaskAsync("Buy milk", isComplete: false);
        await InsertTaskAsync("Read docs", isComplete: true);

        var response = await client.GetAsync("/tasks?status=all");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var tasks = await response.Content.ReadFromJsonAsync<ListTaskResponse[]>();
        Assert.NotNull(tasks);
        Assert.Equal(2, tasks.Length);
    }

    [Fact]
    public async Task GetTasks_rejects_invalid_status_filter()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/tasks?status=archived");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
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

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
