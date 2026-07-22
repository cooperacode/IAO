using TodoApp.Api.Features.CompleteTask;

namespace TodoApp.UnitTests;

public class EmailNotifierTests
{
    [Fact]
    public async Task NotifyTaskStatusChangedAsync_does_not_send_when_email_notifications_are_disabled()
    {
        var sender = new CapturingTaskStatusEmailSender();
        var notifier = new EmailNotifier(EmailNotificationOptions.Disabled, sender);

        await notifier.NotifyTaskStatusChangedAsync(new TaskStatusChangedNotification(
            42,
            "Pay invoices",
            "pending",
            "completed"));

        Assert.Empty(sender.SentMessages);
    }

    [Fact]
    public async Task NotifyTaskStatusChangedAsync_builds_and_sends_status_change_email()
    {
        var sender = new CapturingTaskStatusEmailSender();
        var notifier = new EmailNotifier(
            new EmailNotificationOptions(true, "todo-api@example.test", "operations@example.test"),
            sender);

        await notifier.NotifyTaskStatusChangedAsync(new TaskStatusChangedNotification(
            42,
            "Pay invoices",
            "pending",
            "completed"));

        var message = Assert.Single(sender.SentMessages);
        Assert.Equal("todo-api@example.test", message.SenderAddress);
        Assert.Equal("operations@example.test", message.RecipientAddress);
        Assert.Equal("Task 42 status changed", message.Subject);
        Assert.Contains("Task id: 42", message.Body);
        Assert.Contains("Title: Pay invoices", message.Body);
        Assert.Contains("Previous status: pending", message.Body);
        Assert.Contains("New status: completed", message.Body);
    }

    private sealed class CapturingTaskStatusEmailSender : ITaskStatusEmailSender
    {
        public List<TaskStatusEmailMessage> SentMessages { get; } = [];

        public Task SendAsync(TaskStatusEmailMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }
    }
}
