using Npgsql;
using TodoApp.Api.Shared;
using TodoApp.Api.Tasks.AddTask;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(_ =>
    NpgsqlDataSource.Create(TodoDb.GetConnectionString(builder.Configuration)));

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { Name = "TodoApp API" }));
app.MapAddTask();

app.Run();

public partial class Program;
