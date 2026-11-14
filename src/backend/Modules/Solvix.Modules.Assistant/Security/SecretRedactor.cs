namespace Solvix.Modules.Assistant.Security;

using System.Text.RegularExpressions;

public sealed record RedactionResult(string Text, int Count);

/// <summary>
/// Defence in depth: nothing that looks like a credential is sent to the model, stored in
/// conversation history, indexed in the knowledge base or written to the audit log.
/// Fleet DTOs carry no secrets by design; this guards against data that slips in anyway
/// (pasted passwords, runbooks, device-reported strings).
/// </summary>
public static class SecretRedactor
{
    public const string Placeholder = "[REDACTED]";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (Build(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----"), "[REDACTED PRIVATE KEY]"),
        (Build(@"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}"), "[REDACTED JWT]"),
        (Build(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}", RegexOptions.IgnoreCase), "Bearer " + Placeholder),
        (Build(@"\bAKIA[0-9A-Z]{16}\b"), Placeholder),
        (Build(@"\bsk-[A-Za-z0-9_-]{16,}"), Placeholder),
        (Build(@"\bgh[pousr]_[A-Za-z0-9]{20,}"), Placeholder),
        (Build(@"\bxox[abprs]-[A-Za-z0-9-]{10,}"), Placeholder),
        (Build(@"(?<=://)[^/\s:@]+:[^/\s@]+(?=@)"), Placeholder),
        // key = value / key: value  (quoted or bare)
        (Build(
            @"(?<key>\b(?:password|passwd|pwd|passphrase|secret|client[_-]?secret|api[_-]?key|apikey|access[_-]?key|secret[_-]?key|auth[_-]?token|access[_-]?token|refresh[_-]?token|token|private[_-]?key|credentials?)\b[""']?\s*[:=]\s*)(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s;,""']+)",
            RegexOptions.IgnoreCase),
         "${key}" + Placeholder),
        // "my password is hunter2", "the pin: 1234"
        (Build(@"(?<key>\b(?:password|passcode|passphrase|pin)\s+(?:is|was)\s+)\S+", RegexOptions.IgnoreCase), "${key}" + Placeholder),
    ];

    public static RedactionResult Redact(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return new RedactionResult(string.Empty, 0);
        }

        var count = 0;
        var text = input;
        foreach (var (pattern, replacement) in Rules)
        {
            try
            {
                text = pattern.Replace(text, match =>
                {
                    if (match.Value.Contains(Placeholder, StringComparison.Ordinal))
                    {
                        return match.Value;
                    }

                    count++;
                    return match.Result(replacement);
                });
            }
            catch (RegexMatchTimeoutException)
            {
                // Pathological input: fail closed rather than leak.
                return new RedactionResult(Placeholder, count + 1);
            }
        }

        return new RedactionResult(text, count);
    }

    public static string RedactText(string? input) => Redact(input).Text;

    private static Regex Build(string pattern, RegexOptions options = RegexOptions.None) =>
        new(pattern, options | RegexOptions.Compiled | RegexOptions.CultureInvariant, Timeout);
}
