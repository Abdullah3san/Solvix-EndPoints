namespace Solvix.Infrastructure.Email;

using System.ComponentModel.DataAnnotations;

public sealed class EmailOptions
{
    public const string SectionName = "Solvix:Email";

    [Required]
    [EmailAddress]
    public string FromAddress { get; set; } = "no-reply@solvix.local";

    [Required]
    public string FromName { get; set; } = "Solvix";

    /// <summary>
    /// Write complete messages, including their one-time links, to the log when
    /// no SMTP server is configured. For local development only: anyone who can
    /// read the log can use the links. Ignored outside the Development environment.
    /// </summary>
    public bool LogMessageBodies { get; set; }

    public SmtpOptions Smtp { get; set; } = new();
}

public sealed class SmtpOptions
{
    /// <summary>Leave empty to disable SMTP delivery (messages are then only logged).</summary>
    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 587;

    public string? Username { get; set; }

    public string? Password { get; set; }

    /// <summary>"StartTls" (default), "SslOnConnect" or "None" (local test servers only).</summary>
    public string Security { get; set; } = "StartTls";
}
