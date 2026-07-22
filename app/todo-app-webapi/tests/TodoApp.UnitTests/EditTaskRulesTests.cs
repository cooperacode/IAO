using TodoApp.Api.Features.EditTask;

namespace TodoApp.UnitTests;

public class EditTaskRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalizeTitle_rejects_missing_or_empty_title(string? title)
    {
        var accepted = EditTaskRules.TryNormalizeTitle(title, out var normalizedTitle);

        Assert.False(accepted);
        Assert.Equal(string.Empty, normalizedTitle);
    }

    [Fact]
    public void TryNormalizeTitle_accepts_and_trims_title()
    {
        var accepted = EditTaskRules.TryNormalizeTitle("  Updated title  ", out var normalizedTitle);

        Assert.True(accepted);
        Assert.Equal("Updated title", normalizedTitle);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "completed")]
    public void ToStatus_projects_completion_state(bool isComplete, string expectedStatus)
    {
        Assert.Equal(expectedStatus, EditTaskRules.ToStatus(isComplete));
    }
}
