using TodoApp.Api.Tasks.AddTask;

namespace TodoApp.UnitTests;

public class AddTaskRulesTests
{
    [Fact]
    public void ValidateTitle_accepts_and_trims_non_empty_title()
    {
        var result = AddTaskRules.ValidateTitle("  Buy milk  ");

        Assert.True(result.IsValid);
        Assert.Equal("Buy milk", result.Title);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateTitle_rejects_missing_title(string? title)
    {
        var result = AddTaskRules.ValidateTitle(title);

        Assert.False(result.IsValid);
        Assert.Null(result.Title);
        Assert.Equal("Title is required.", result.Error);
    }
}
