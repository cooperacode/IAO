namespace TodoApp.Api.Features.CompleteTask;

public sealed record EmailNotificationOptions(
    bool IsEnabled,
    string SenderAddress,
    string RecipientAddress)
{
    public const string SenderEnvironmentVariable = "TODO_EMAIL_FROM";
    public const string RecipientEnvironmentVariable = "TODO_EMAIL_TO";

    public static EmailNotificationOptions Disabled { get; } = new(false, "", "");

    public static EmailNotificationOptions Resolve(string? senderAddress, string? recipientAddress)
    {
        var normalizedSenderAddress = Normalize(senderAddress);
        var normalizedRecipientAddress = Normalize(recipientAddress);

        if (normalizedSenderAddress is null && normalizedRecipientAddress is null)
        {
            return Disabled;
        }

        if (normalizedSenderAddress is null || normalizedRecipientAddress is null)
        {
            throw new InvalidOperationException(
                $"Configure both {SenderEnvironmentVariable} and {RecipientEnvironmentVariable}, or leave both empty to disable e-mail notifications.");
        }

        return new EmailNotificationOptions(true, normalizedSenderAddress, normalizedRecipientAddress);
    }

    private static string? Normalize(string? address)
    {
        return string.IsNullOrWhiteSpace(address) ? null : address.Trim();
    }
}
