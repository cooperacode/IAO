using TodoApp.Api.Tasks.CompleteTask;

namespace TodoApp.UnitTests;

public class CompleteTaskRulesTests
{
    [Fact]
    public void ToStatus_returns_completed_for_completed_task()
    {
        Assert.Equal("completed", CompleteTaskRules.ToStatus(isCompleted: true));
    }
}
