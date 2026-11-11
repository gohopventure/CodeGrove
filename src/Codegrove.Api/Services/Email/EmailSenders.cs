using Azure;
using Azure.Communication.Email;

namespace Codegrove.Api.Services.Email;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default);
}

/// <summary>Development sender: writes the email to the log instead of sending it.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default)
    {
        logger.LogInformation("DEV EMAIL to {To}\nSubject: {Subject}\n{Body}", to, subject, plainText);
        return Task.CompletedTask;
    }
}

public sealed class AcsEmailOptions
{
    public string ConnectionString { get; set; } = "";
    /// <summary>A verified sender on your ACS email domain, e.g. DoNotReply@mail.codegrove.example.</summary>
    public string SenderAddress { get; set; } = "";
}

/// <summary>Production sender using Azure Communication Services Email.</summary>
public sealed class AcsEmailSender(AcsEmailOptions options, ILogger<AcsEmailSender> logger) : IEmailSender
{
    private readonly EmailClient _client = new(options.ConnectionString);

    public async Task SendAsync(string to, string subject, string html, string plainText, CancellationToken ct = default)
    {
        var message = new EmailMessage(
            senderAddress: options.SenderAddress,
            recipientAddress: to,
            content: new EmailContent(subject) { Html = html, PlainText = plainText });

        // WaitUntil.Started: don't block the request on delivery; ACS retries internally.
        var operation = await _client.SendAsync(WaitUntil.Started, message, ct);
        logger.LogInformation("Queued email {OperationId}", operation.Id);
    }
}
