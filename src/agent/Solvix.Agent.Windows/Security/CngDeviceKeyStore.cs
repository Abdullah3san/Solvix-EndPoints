namespace Solvix.Agent.Windows.Security;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Solvix.Agent.Core.Security;

/// <summary>
/// Persisted, non-exportable ECDSA P-256 machine keys. Prefers the TPM
/// (Microsoft Platform Crypto Provider) so the key cannot be copied off the
/// device; falls back to the software KSP (keys protected by DPAPI/LSA).
/// Key names carry the provider: <c>tpm:...</c> or <c>ksp:...</c>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class CngDeviceKeyStore(ILogger<CngDeviceKeyStore> logger) : IDeviceKeyStore
{
    private static readonly CngProvider Tpm = new("Microsoft Platform Crypto Provider");
    private static readonly CngProvider Software = CngProvider.MicrosoftSoftwareKeyStorageProvider;

    public string Description { get; private set; } = "CNG";

    public ECDsa CreateKey(out string keyName)
    {
        var baseName = "Solvix-Agent-Device-" + Guid.NewGuid().ToString("N");
        foreach (var (prefix, provider) in new[] { ("tpm", Tpm), ("ksp", Software) })
        {
            try
            {
                var key = CngKey.Create(CngAlgorithm.ECDsaP256, baseName, new CngKeyCreationParameters
                {
                    Provider = provider,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing,
                });
                keyName = $"{prefix}:{baseName}";
                Description = prefix == "tpm" ? "TPM (Platform Crypto Provider)" : "Software KSP";
                LogCreated(logger, Description);
                return new ECDsaCng(key);
            }
            catch (CryptographicException ex) when (prefix == "tpm")
            {
                LogTpmUnavailable(logger, ex.Message);
            }
        }

        throw new CryptographicException("No key storage provider could create the device key.");
    }

    public ECDsa OpenKey(string keyName)
    {
        var (provider, name) = Parse(keyName);
        return new ECDsaCng(CngKey.Open(name, provider, CngKeyOpenOptions.MachineKey));
    }

    public bool Exists(string keyName)
    {
        var (provider, name) = Parse(keyName);
        return CngKey.Exists(name, provider, CngKeyOpenOptions.MachineKey);
    }

    public void DeleteKey(string keyName)
    {
        if (!Exists(keyName))
        {
            return;
        }

        var (provider, name) = Parse(keyName);
        using var key = CngKey.Open(name, provider, CngKeyOpenOptions.MachineKey);
        key.Delete();
    }

    private static (CngProvider Provider, string Name) Parse(string keyName) =>
        keyName.Split(':', 2) switch
        {
            ["tpm", var name] => (Tpm, name),
            ["ksp", var name] => (Software, name),
            _ => throw new ArgumentException("Unknown key reference.", nameof(keyName)),
        };

    [LoggerMessage(Level = LogLevel.Information, Message = "Created device key in {Provider}")]
    private static partial void LogCreated(ILogger logger, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "TPM key storage unavailable ({Reason}); using the software key storage provider")]
    private static partial void LogTpmUnavailable(ILogger logger, string reason);
}

/// <summary>DPAPI (machine scope) protection for the bootstrap enrollment token.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Solvix.Agent.Bootstrap.v1"u8.ToArray();

    public byte[] Protect(byte[] plaintext) => ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.LocalMachine);

    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.LocalMachine);
}
