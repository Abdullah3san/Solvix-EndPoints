namespace Solvix.SharedKernel.Email;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);

/// <summary>Delivers one message. Implementations throw on failure.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Hands a message to the background dispatcher and returns immediately, so the
/// time a request takes never depends on whether an e-mail was sent (which
/// would otherwise reveal whether an address has an account).
/// </summary>
public interface IEmailQueue
{
    void Enqueue(EmailMessage message);
}
