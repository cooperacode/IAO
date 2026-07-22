using TodoApp.Api.Features.CompleteTask;

namespace TodoApp.UnitTests;

public class EmailNotificationOptionsTests
{
    [Fact]
    public void Resolve_disables_email_notifications_when_addresses_are_missing()
    {
        var options = EmailNotificationOptions.Resolve(null, "   ");

        Assert.False(options.IsEnabled);
        Assert.Equal("", options.SenderAddress);
        Assert.Equal("", options.RecipientAddress);
    }

    [Fact]
    public void Resolve_uses_configured_sender_and_recipient_when_both_are_present()
    {
        var options = EmailNotificationOptions.Resolve(
            "  todo-api@example.test  ",
            "  operations@example.test  ");

        Assert.True(options.IsEnabled);
        Assert.Equal("todo-api@example.test", options.SenderAddress);
        Assert.Equal("operations@example.test", options.RecipientAddress);
    }

    [Theory]
    [InlineData(null, "operations@example.test")]
    [InlineData("todo-api@example.test", null)]
    public void Resolve_rejects_partial_email_configuration(string? senderAddress, string? recipientAddress)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EmailNotificationOptions.Resolve(senderAddress, recipientAddress));

        Assert.Contains(EmailNotificationOptions.SenderEnvironmentVariable, exception.Message);
        Assert.Contains(EmailNotificationOptions.RecipientEnvironmentVariable, exception.Message);
    }
}
