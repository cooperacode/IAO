using TodoApp.Api.Tasks.EditTask;

namespace TodoApp.UnitTests;

public class EditTaskRulesTests
{
    [Fact]
    public void ValidateTitle_accepts_and_trims_non_empty_title()
    {
        var result = EditTaskRules.ValidateTitle("  Updated title  ");

        Assert.True(result.IsValid);
        Assert.Equal("Updated title", result.Title);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateTitle_rejects_missing_title(string? title)
    {
        var result = EditTaskRules.ValidateTitle(title);

        Assert.False(result.IsValid);
        Assert.Null(result.Title);
        Assert.Equal("Title is required.", result.Error);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "completed")]
    public void ToStatus_maps_completion_flag_to_api_status(bool isCompleted, string expectedStatus)
    {
        Assert.Equal(expectedStatus, EditTaskRules.ToStatus(isCompleted));
    }
}
