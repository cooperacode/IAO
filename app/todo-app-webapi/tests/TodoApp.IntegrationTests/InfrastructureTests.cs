using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public class InfrastructureTests
{
    [Fact]
    public async Task Startup_creates_tasks_table_in_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var health = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);

        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var command = dataSource.CreateCommand("""
            SELECT COUNT(*)
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name = 'tasks'
              AND column_name IN ('id', 'title', 'is_complete', 'created_at');
            """);

        var columnCount = Convert.ToInt32(await command.ExecuteScalarAsync());

        Assert.Equal(4, columnCount);
    }
}
