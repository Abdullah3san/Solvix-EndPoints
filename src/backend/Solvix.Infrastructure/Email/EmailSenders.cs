namespace Solvix.Infrastructure.Email;

using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Solvix.SharedKernel.Email;

/// <summary>Delivers mail through the configured SMTP server.</summary>
internal sealed class SmtpEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly EmailOptions _options = options.Value;

    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        using var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody }.ToMessageBody();

        var security = _options.Smtp.Security?.Trim().ToUpperInvariant() switch
        {
            "NONE" => SecureSocketOptions.None,
            "SSLONCONNECT" => SecureSocketOptions.SslOnConnect,
            _ => SecureSocketOptions.StartTls,
        };

        var host = _options.Smtp.Host
            ?? throw new InvalidOperationException($"{EmailOptions.SectionName}:Smtp:Host is not configured.");
        using var client = new SmtpClient();
        await client.ConnectAsync(host, _options.Smtp.Port, security, cancellationToken);
        if (!string.IsNullOrEmpty(_options.Smtp.Username))
        {
            await client.AuthenticateAsync(_options.Smtp.Username, _options.Smtp.Password ?? string.Empty, cancellationToken);
        }

        await client.SendAsync(mime, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);
    }
}

/// <summary>
/// Used when no SMTP server is configured. Logs that a message would have been
/// sent; the body (which contains one-time links) is only written in the
/// Development environment and only when explicitly enabled.
/// </summary>
internal sealed partial class LoggingEmailSender(IOptions<EmailOptions> options, IHostEnvironment environment, ILogger<LoggingEmailSender> logger)
    : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (options.Value.LogMessageBodies && environment.IsDevelopment())
        {
            LogMessageWithBody(logger, message.To, message.Subject, message.TextBody);
        }
        else
        {
            LogMessage(logger, message.To, message.Subject);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No SMTP server configured: e-mail to {To} with subject '{Subject}' was not delivered")]
    private static partial void LogMessage(ILogger logger, string to, string subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "DEVELOPMENT e-mail to {To}: {Subject}\n{Body}")]
    private static partial void LogMessageWithBody(ILogger logger, string to, string subject, string body);
}
