using Npgsql;
using TodoApp.Api.Shared;

var builder = WebApplication.CreateBuilder(args);

var connectionString = Database.ResolveConnectionString(
    builder.Configuration[Database.ConnectionStringEnvironmentVariable]);
var dataSource = NpgsqlDataSource.Create(connectionString);
builder.Services.AddSingleton(dataSource);

var app = builder.Build();

await Database.EnsureCreatedAsync(dataSource);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

public partial class Program;
