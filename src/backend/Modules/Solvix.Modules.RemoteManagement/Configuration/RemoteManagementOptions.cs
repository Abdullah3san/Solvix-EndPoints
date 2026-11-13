namespace Solvix.Modules.RemoteManagement.Configuration;

public sealed class RemoteManagementOptions
{
    public const string SectionName = "Solvix:RemoteManagement";

    public CommandSigningOptions CommandSigning { get; set; } = new();

    /// <summary>How long an agent's command poll is held open when nothing is pending (max 30).</summary>
    public int LongPollSeconds { get; set; } = 25;

    /// <summary>A delivered command that was not acknowledged within this time is offered again.</summary>
    public int RedeliveryAfterSeconds { get; set; } = 60;

    /// <summary>Commands handed out per poll.</summary>
    public int MaxBatchSize { get; set; } = 10;

    /// <summary>Interval of the expiry / timeout sweep.</summary>
    public int SweepIntervalSeconds { get; set; } = 15;

    /// <summary>Tests drive the sweeper explicitly.</summary>
    public bool RunSweeper { get; set; } = true;
}

public sealed class CommandSigningOptions
{
    /// <summary>PEM file with the ECDSA P-256 private key (PKCS#8 or SEC1).</summary>
    public string? KeyPath { get; set; }

    /// <summary>The same key as PEM text (e.g. from a secret store); takes precedence over <see cref="KeyPath"/>.</summary>
    public string? KeyPem { get; set; }

    /// <summary>Development / evaluation: create the key at <see cref="KeyPath"/> on first start.</summary>
    public bool AutoGenerate { get; set; }

    /// <summary>Public keys (SPKI, base64) of retired signing keys agents should still accept during a rollover.</summary>
    public string[] AdditionalPublicKeys { get; set; } = [];
}
