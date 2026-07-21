namespace TodoApp.Api.Shared;

public static class TodoDb
{
    public const string DefaultConnectionString =
        "Host=localhost;Port=54329;Database=todoapp;Username=todoapp;Password=todoapp";

    public static string GetConnectionString(IConfiguration configuration) =>
        configuration["TODO_DB_CONNECTION"] ?? DefaultConnectionString;
}
