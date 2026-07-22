namespace TodoApp.Api.Features.CompleteTask;

public sealed class EmailNotifier
{
    private readonly EmailNotificationOptions _options;
    private readonly ITaskStatusEmailSender _sender;

    public EmailNotifier(EmailNotificationOptions options, ITaskStatusEmailSender sender)
    {
        _options = options;
        _sender = sender;
    }

    public async Task NotifyTaskStatusChangedAsync(
        TaskStatusChangedNotification notification,
        CancellationToken cancellationToken = default)
    {
        if (!_options.IsEnabled)
        {
            return;
        }

        await _sender.SendAsync(CreateMessage(notification), cancellationToken);
    }

    private TaskStatusEmailMessage CreateMessage(TaskStatusChangedNotification notification)
    {
        return new TaskStatusEmailMessage(
            _options.SenderAddress,
            _options.RecipientAddress,
            $"Task {notification.TaskId} status changed",
            $"""
            Task status changed.
            Task id: {notification.TaskId}
            Title: {notification.Title}
            Previous status: {notification.PreviousStatus}
            New status: {notification.NewStatus}
            """);
    }
}

public interface ITaskStatusEmailSender
{
    Task SendAsync(TaskStatusEmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class NoOpTaskStatusEmailSender : ITaskStatusEmailSender
{
    public Task SendAsync(TaskStatusEmailMessage message, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}

public sealed record TaskStatusChangedNotification(
    long TaskId,
    string Title,
    string PreviousStatus,
    string NewStatus);

public sealed record TaskStatusEmailMessage(
    string SenderAddress,
    string RecipientAddress,
    string Subject,
    string Body);
