namespace Solvix.Agent.Core.Security;

using System.Security.Cryptography;

/// <summary>
/// Holds the device's private keys. On Windows keys are persisted, non-exportable
/// CNG machine keys (TPM-backed when available); the agent never sees key bytes.
/// </summary>
public interface IDeviceKeyStore
{
    /// <summary>Human-readable description used in diagnostics (e.g. "TPM", "Software KSP").</summary>
    string Description { get; }

    /// <summary>Creates a new ECDSA P-256 key and returns its handle and name.</summary>
    ECDsa CreateKey(out string keyName);

    ECDsa OpenKey(string keyName);

    bool Exists(string keyName);

    void DeleteKey(string keyName);
}

/// <summary>
/// File-based key store for non-Windows development and tests. Keys are PKCS#8
/// PEM files readable by the owner only. Not used on Windows.
/// </summary>
public sealed class FileDeviceKeyStore(string directory) : IDeviceKeyStore
{
    public string Description => "File (development)";

    public ECDsa CreateKey(out string keyName)
    {
        keyName = "solvix-device-" + Guid.NewGuid().ToString("N");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        State.AtomicFile.WriteAllText(PathFor(keyName), key.ExportPkcs8PrivateKeyPem());
        return key;
    }

    public ECDsa OpenKey(string keyName)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(PathFor(keyName)));
        return key;
    }

    public bool Exists(string keyName) => File.Exists(PathFor(keyName));

    public void DeleteKey(string keyName)
    {
        var path = PathFor(keyName);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string PathFor(string keyName)
    {
        if (keyName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-')))
        {
            throw new ArgumentException("Invalid key name.", nameof(keyName));
        }

        return Path.Combine(directory, "keys", keyName + ".pem");
    }
}

/// <summary>Protects the bootstrap enrollment token at rest (DPAPI machine scope on Windows).</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);

    byte[] Unprotect(byte[] protectedData);
}

/// <summary>No-op protector for non-Windows development; relies on file permissions.</summary>
public sealed class FilePermissionSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plaintext) => plaintext;

    public byte[] Unprotect(byte[] protectedData) => protectedData;
}
