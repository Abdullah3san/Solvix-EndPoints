namespace Solvix.Agent.Core.Security;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.State;

/// <summary>
/// The short-lived enrollment token supplied at install time. It is stored
/// encrypted (DPAPI machine scope on Windows), read once for enrollment and
/// deleted afterwards. It is never part of the installer package.
/// </summary>
public sealed class BootstrapTokenStore(IOptions<AgentOptions> options, ISecretProtector protector)
{
    private string FilePath => Path.Combine(options.Value.ResolveDataDirectory(), "enrollment.token");

    public bool HasToken => File.Exists(FilePath);

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        try
        {
            AtomicFile.WriteAllBytes(FilePath, protector.Protect(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string? Read()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        var bytes = protector.Unprotect(File.ReadAllBytes(FilePath));
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Delete()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    /// <summary>Moves a rejected token aside so it is not retried until an administrator supplies a new one.</summary>
    public void MarkRejected()
    {
        if (File.Exists(FilePath))
        {
            File.Move(FilePath, FilePath + ".rejected", overwrite: true);
        }
    }
}
