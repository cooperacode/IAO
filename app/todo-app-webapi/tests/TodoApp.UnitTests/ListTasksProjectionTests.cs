using TodoApp.Api.Features.ListTasks;

namespace TodoApp.UnitTests;

public class ListTasksProjectionTests
{
    [Theory]
    [InlineData(null, TaskStatusFilter.All)]
    [InlineData("", TaskStatusFilter.All)]
    [InlineData("all", TaskStatusFilter.All)]
    [InlineData("PENDING", TaskStatusFilter.Pending)]
    [InlineData("completed", TaskStatusFilter.Completed)]
    public void TryParseStatusFilter_accepts_supported_statuses(string? status, TaskStatusFilter expected)
    {
        var accepted = ListTasksProjection.TryParseStatusFilter(status, out var statusFilter);

        Assert.True(accepted);
        Assert.Equal(expected, statusFilter);
    }

    [Fact]
    public void TryParseStatusFilter_rejects_unknown_status()
    {
        var accepted = ListTasksProjection.TryParseStatusFilter("archived", out _);

        Assert.False(accepted);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "completed")]
    public void ToResponse_projects_database_status_to_api_status(bool isComplete, string expectedStatus)
    {
        var response = ListTasksProjection.ToResponse(42, "Read docs", isComplete);

        Assert.Equal(42, response.Id);
        Assert.Equal("Read docs", response.Title);
        Assert.Equal(expectedStatus, response.Status);
    }
}
