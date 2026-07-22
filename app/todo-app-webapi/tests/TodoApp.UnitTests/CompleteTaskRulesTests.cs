using TodoApp.Api.Features.CompleteTask;

namespace TodoApp.UnitTests;

public class CompleteTaskRulesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyTransition_marks_task_complete(bool currentlyComplete)
    {
        Assert.True(CompleteTaskRules.ApplyTransition(currentlyComplete));
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "completed")]
    public void ToStatus_projects_completion_state(bool isComplete, string expectedStatus)
    {
        Assert.Equal(expectedStatus, CompleteTaskRules.ToStatus(isComplete));
    }
}
