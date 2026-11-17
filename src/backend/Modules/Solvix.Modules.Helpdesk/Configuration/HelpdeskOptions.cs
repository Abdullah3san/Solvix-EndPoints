namespace Solvix.Modules.Helpdesk.Configuration;

using Microsoft.Extensions.Options;

/// <summary>Bound from <c>Solvix:Helpdesk</c>.</summary>
public sealed class HelpdeskOptions
{
    public const string SectionName = "Solvix:Helpdesk";

    public AttachmentOptions Attachments { get; set; } = new();

    public EmailOptions Email { get; set; } = new();

    public AiOptions Ai { get; set; } = new();

    public HelpdeskWorkerOptions Worker { get; set; } = new();
}

public sealed class AttachmentOptions
{
    public int MaxBytes { get; set; } = 10 * 1024 * 1024;

    public int MaxPerTicket { get; set; } = 25;
}

public sealed class EmailOptions
{
    /// <summary>Tickets one sender address may open per channel per hour before further mail is dropped (mail-bomb guard).</summary>
    public int MaxTicketsPerSenderPerHour { get; set; } = 20;

    public int MaxBodyChars { get; set; } = 20_000;

    public int MaxAttachments { get; set; } = 5;
}

public sealed class AiOptions
{
    /// <summary><c>none</c> (built-in extractive summary) or <c>anthropic</c>.</summary>
    public string Provider { get; set; } = "none";

    public string? ApiKey { get; set; }

    public string Endpoint { get; set; } = "https://api.anthropic.com";

    public string Model { get; set; } = "claude-haiku-5-5";

    public int MaxInputChars { get; set; } = 12_000;

    /// <summary>Internal notes are NOT sent to the model unless this is enabled.</summary>
    public bool IncludeInternalNotes { get; set; }

    public int CooldownSeconds { get; set; } = 30;
}

public sealed class HelpdeskWorkerOptions
{
    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;
}

internal sealed class HelpdeskOptionsValidator : IValidateOptions<HelpdeskOptions>
{
    public ValidateOptionsResult Validate(string? name, HelpdeskOptions options)
    {
        var errors = new List<string>();
        var provider = options.Ai.Provider.Trim().ToLowerInvariant();
        if (provider is not ("none" or "anthropic"))
        {
            errors.Add("Solvix:Helpdesk:Ai:Provider must be 'none' or 'anthropic'.");
        }

        if (provider == "anthropic" && string.IsNullOrWhiteSpace(options.Ai.ApiKey))
        {
            errors.Add("Solvix:Helpdesk:Ai:ApiKey is required when the AI provider is 'anthropic'.");
        }

        if (options.Attachments.MaxBytes is < 1024 or > 25 * 1024 * 1024)
        {
            errors.Add("Solvix:Helpdesk:Attachments:MaxBytes must be between 1 KiB and 25 MiB.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
