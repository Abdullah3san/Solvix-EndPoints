namespace Solvix.IntegrationTests.Fixtures;

using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Solvix.SharedKernel.Email;

/// <summary>
/// Replaces SMTP delivery in tests. Messages still travel through the real
/// queue and background dispatcher; this is only the last hop. Tests read the
/// one-time links out of the captured mail exactly as a recipient would.
/// </summary>
public sealed partial class CapturingEmailSender : IEmailSender
{
    private readonly ConcurrentQueue<EmailMessage> _messages = new();

    public IReadOnlyCollection<EmailMessage> Messages => [.. _messages];

    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    /// <summary>Waits for the newest message to <paramref name="to"/> whose subject contains <paramref name="subjectContains"/>.</summary>
    public async Task<EmailMessage> WaitForAsync(string to, string subjectContains, int skip = 0, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var matches = _messages
                .Where(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count > skip)
            {
                return matches[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"No e-mail to {to} with subject containing '{subjectContains}' arrived. Captured: {string.Join(" | ", _messages.Select(m => $"{m.To}: {m.Subject}"))}");
            }

            await Task.Delay(25);
        }
    }

    public int Count(string to, string subjectContains) =>
        _messages.Count(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase) && m.Subject.Contains(subjectContains, StringComparison.OrdinalIgnoreCase));

    /// <summary>Extracts the token from the link in a message (<c>…/path#token=…</c>).</summary>
    public static string TokenFrom(EmailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var match = TokenPattern().Match(message.TextBody);
        Assert.True(match.Success, $"No token link in e-mail body: {message.TextBody}");
        return Uri.UnescapeDataString(match.Groups[1].Value);
    }

    [GeneratedRegex(@"#token=([A-Za-z0-9_\-%]+)")]
    private static partial Regex TokenPattern();
}
