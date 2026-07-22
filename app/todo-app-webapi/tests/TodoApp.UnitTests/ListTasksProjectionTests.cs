using TodoApp.Api.Features.ListTasks;

namespace TodoApp.UnitTests;

public class ListTasksProjectionTests
{
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
