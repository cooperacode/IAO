using TodoApp.Api.Features.RemoveTask;

namespace TodoApp.UnitTests;

public class RemoveTaskRulesTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(0, false)]
    public void WasRemoved_returns_true_only_when_delete_affected_rows(int rowsDeleted, bool expected)
    {
        Assert.Equal(expected, RemoveTaskRules.WasRemoved(rowsDeleted));
    }
}
