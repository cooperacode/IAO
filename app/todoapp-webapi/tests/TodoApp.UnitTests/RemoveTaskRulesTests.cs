using TodoApp.Api.Tasks.RemoveTask;

namespace TodoApp.UnitTests;

public class RemoveTaskRulesTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void WasRemoved_maps_affected_rows_to_result(int rowsAffected, bool expected)
    {
        Assert.Equal(expected, RemoveTaskRules.WasRemoved(rowsAffected));
    }
}
