using Npgsql;
using TodoApp.Api.Shared;

namespace TodoApp.IntegrationTests;

public abstract class IntegrationTestBase : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await using var dataSource = CreateDataSource();
        await Database.EnsureCreatedAsync(dataSource);
        await using var command = dataSource.CreateCommand("TRUNCATE TABLE tasks RESTART IDENTITY;");
        await command.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    private static NpgsqlDataSource CreateDataSource()
    {
        var connectionString = Database.ResolveConnectionString(
            Environment.GetEnvironmentVariable(Database.ConnectionStringEnvironmentVariable));
        return NpgsqlDataSource.Create(connectionString);
    }
}
