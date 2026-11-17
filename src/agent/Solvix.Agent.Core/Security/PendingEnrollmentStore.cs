namespace Solvix.Agent.Core.Security;

using System.Text.Json;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;

/// <summary>The request an administrator still has to approve: which key was generated and the secret to poll with.</summary>
public sealed record PendingEnrollmentState(string KeyName, string PollToken, int PollIntervalSeconds, DateTimeOffset ExpiresAtUtc);

/// <summary>Poll token and key name of a pending enrollment, encrypted at rest like the enrollment token.</summary>
public sealed class PendingEnrollmentStore(IOptions<AgentOptions> options, ISecretProtector protector)
{
    private string FilePath => Path.Combine(options.Value.ResolveDataDirectory(), "enrollment.pending");

    public bool Exists => File.Exists(FilePath);

    public PendingEnrollmentState? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        try
        {
            var bytes = protector.Unprotect(File.ReadAllBytes(FilePath));
            return JsonSerializer.Deserialize<PendingEnrollmentState>(bytes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    public void Save(PendingEnrollmentState state) =>
        AtomicFile.WriteAllBytes(FilePath, protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state)));

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
