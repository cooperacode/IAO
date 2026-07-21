using TodoApp.Api.Tasks.ListTasks;

namespace TodoApp.UnitTests;

public class ListTaskRulesTests
{
    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "completed")]
    public void ToStatus_maps_completion_flag_to_api_status(bool isCompleted, string expectedStatus)
    {
        Assert.Equal(expectedStatus, ListTaskRules.ToStatus(isCompleted));
    }
}
