using TodoApp.Api.Features.AddTask;

namespace TodoApp.UnitTests;

public class AddTaskRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeTitle_rejects_missing_or_empty_title(string? title)
    {
        var accepted = AddTaskRules.TryNormalizeTitle(title, out var normalizedTitle);

        Assert.False(accepted);
        Assert.Equal(string.Empty, normalizedTitle);
    }

    [Fact]
    public void TryNormalizeTitle_accepts_and_trims_title()
    {
        var accepted = AddTaskRules.TryNormalizeTitle("  Buy milk  ", out var normalizedTitle);

        Assert.True(accepted);
        Assert.Equal("Buy milk", normalizedTitle);
    }
}
