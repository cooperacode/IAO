using TodoApp.Api.Shared;

namespace TodoApp.UnitTests;

public class DatabaseConfigurationTests
{
    [Fact]
    public void ResolveConnectionString_uses_default_when_value_is_missing()
    {
        Assert.Equal(Database.DefaultConnectionString, Database.ResolveConnectionString(null));
        Assert.Equal(Database.DefaultConnectionString, Database.ResolveConnectionString("   "));
    }

    [Fact]
    public void ResolveConnectionString_uses_configured_value_when_present()
    {
        const string configured = "Host=db;Database=custom;Username=user;Password=secret";

        Assert.Equal(configured, Database.ResolveConnectionString($"  {configured}  "));
    }
}
