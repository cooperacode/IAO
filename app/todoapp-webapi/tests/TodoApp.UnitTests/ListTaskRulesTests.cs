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

    [Fact]
    public void ParseStatusFilter_defaults_missing_status_to_all()
    {
        var filter = ListTaskRules.ParseStatusFilter(null);

        Assert.True(filter.IsValid);
        Assert.Equal(ListTaskStatusFilter.All, filter.StatusFilter);
        Assert.Null(filter.Error);
    }

    [Theory]
    [InlineData("all", ListTaskStatusFilter.All)]
    [InlineData("pending", ListTaskStatusFilter.Pending)]
    [InlineData("completed", ListTaskStatusFilter.Completed)]
    [InlineData(" PENDING ", ListTaskStatusFilter.Pending)]
    public void ParseStatusFilter_accepts_supported_statuses(string status, ListTaskStatusFilter expectedFilter)
    {
        var filter = ListTaskRules.ParseStatusFilter(status);

        Assert.True(filter.IsValid);
        Assert.Equal(expectedFilter, filter.StatusFilter);
        Assert.Null(filter.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("done")]
    public void ParseStatusFilter_rejects_invalid_status(string status)
    {
        var filter = ListTaskRules.ParseStatusFilter(status);

        Assert.False(filter.IsValid);
        Assert.Equal("Invalid status. Use pending, completed, or all.", filter.Error);
    }
}
