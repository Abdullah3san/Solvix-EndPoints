namespace Solvix.Modules.Identity.Application.Emails;

using System.Net;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Configuration;
using Solvix.SharedKernel.Email;

/// <summary>
/// Composes and queues the e-mails of the identity flows. Tokens travel in the
/// URL fragment (<c>#token=…</c>): fragments are never sent to a server, so the
/// secret stays out of access logs, proxies and Referer headers.
/// </summary>
public interface IAuthEmails
{
    void SendEmailVerification(string to, string displayName, string token);

    void SendPasswordReset(string to, string displayName, string token);

    void SendInvitation(string to, string tenantName, string inviterName, string token, DateTimeOffset expiresAtUtc);

    void SendAccountLocked(string to, string displayName, DateTimeOffset lockedUntilUtc);

    void SendPasswordChanged(string to, string displayName);

    void SendMfaChanged(string to, string displayName, bool enabled);
}

internal sealed class AuthEmails(IEmailQueue queue, IOptions<IdentitySecurityOptions> options) : IAuthEmails
{
    private readonly IdentitySecurityOptions _options = options.Value;

    public void SendEmailVerification(string to, string displayName, string token) =>
        Send(
            to,
            "Confirm your e-mail address",
            displayName,
            "Confirm your e-mail address to finish setting up your Solvix account.",
            "Confirm e-mail address",
            Link("verify-email", token),
            $"This link expires in {_options.EmailVerificationTokenHours} hours. If you did not create a Solvix account, you can ignore this message.");

    public void SendPasswordReset(string to, string displayName, string token) =>
        Send(
            to,
            "Reset your password",
            displayName,
            "We received a request to reset the password of your Solvix account.",
            "Choose a new password",
            Link("reset-password", token),
            $"This link expires in {_options.PasswordResetTokenMinutes} minutes and can be used once. If you did not ask for it, your password is unchanged and you can ignore this message.");

    public void SendInvitation(string to, string tenantName, string inviterName, string token, DateTimeOffset expiresAtUtc) =>
        Send(
            to,
            $"You have been invited to {tenantName} on Solvix",
            displayName: null,
            $"{inviterName} invited you to join the workspace \"{tenantName}\" on Solvix.",
            "Accept invitation",
            Link("accept-invitation", token),
            $"This invitation expires on {expiresAtUtc:yyyy-MM-dd HH:mm} UTC. If you were not expecting it, you can ignore this message.");

    public void SendAccountLocked(string to, string displayName, DateTimeOffset lockedUntilUtc) =>
        Send(
            to,
            "Your account was temporarily locked",
            displayName,
            $"After several failed sign-in attempts your Solvix account was locked until {lockedUntilUtc:yyyy-MM-dd HH:mm} UTC.",
            actionLabel: null,
            actionUrl: null,
            "If this was you, wait and try again, or reset your password. If it was not, your password still protected the account, but consider changing it and turning on multi-factor authentication.");

    public void SendPasswordChanged(string to, string displayName) =>
        Send(
            to,
            "Your password was changed",
            displayName,
            "The password of your Solvix account was just changed and your other sessions were signed out.",
            actionLabel: null,
            actionUrl: null,
            "If you did not make this change, reset your password immediately and contact your administrator.");

    public void SendMfaChanged(string to, string displayName, bool enabled) =>
        Send(
            to,
            enabled ? "Multi-factor authentication was turned on" : "Multi-factor authentication was turned off",
            displayName,
            enabled
                ? "Multi-factor authentication is now active on your Solvix account."
                : "Multi-factor authentication was turned off on your Solvix account.",
            actionLabel: null,
            actionUrl: null,
            "If you did not make this change, reset your password immediately and contact your administrator.");

    private string Link(string path, string token) =>
        $"{_options.WebBaseUrl.TrimEnd('/')}/{path}#token={Uri.EscapeDataString(token)}";

    private void Send(string to, string subject, string? displayName, string intro, string? actionLabel, string? actionUrl, string footer)
    {
        var greeting = string.IsNullOrWhiteSpace(displayName) ? "Hello," : $"Hello {displayName},";

        var text = $"{greeting}\n\n{intro}\n\n" +
                   (actionUrl is null ? string.Empty : $"{actionLabel}: {actionUrl}\n\n") +
                   $"{footer}\n\n— Solvix";

        // Everything that originates from user input is HTML-encoded.
        var html =
            "<!doctype html><html><body style=\"font-family:system-ui,-apple-system,'Segoe UI',sans-serif;color:#0f172a;line-height:1.5\">" +
            $"<p>{WebUtility.HtmlEncode(greeting)}</p><p>{WebUtility.HtmlEncode(intro)}</p>" +
            (actionUrl is null
                ? string.Empty
                : $"<p><a href=\"{WebUtility.HtmlEncode(actionUrl)}\" style=\"display:inline-block;padding:10px 16px;background:#2563eb;color:#ffffff;border-radius:6px;text-decoration:none\">{WebUtility.HtmlEncode(actionLabel)}</a></p>" +
                  $"<p style=\"font-size:12px;color:#475569\">Or copy this link into your browser:<br>{WebUtility.HtmlEncode(actionUrl)}</p>") +
            $"<p style=\"font-size:13px;color:#475569\">{WebUtility.HtmlEncode(footer)}</p><p>— Solvix</p></body></html>";

        queue.Enqueue(new EmailMessage(to, subject, text, html));
    }
}
