namespace Solvix.Modules.RemoteManagement.CommandBroker;

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Modules.RemoteManagement.Configuration;

/// <summary>
/// Signs command envelopes. The private key never leaves the implementation; a cloud KMS
/// signer can replace <see cref="EcdsaCommandSigner"/> without changes elsewhere.
/// </summary>
public interface ICommandSigner
{
    string KeyId { get; }

    SignedCommand Sign(CommandEnvelope envelope);

    /// <summary>Every public key agents should currently accept (active first).</summary>
    CommandKeySet KeySet { get; }
}

public sealed partial class EcdsaCommandSigner : ICommandSigner, IDisposable
{
    private readonly ECDsa _key;

    public EcdsaCommandSigner(IOptions<RemoteManagementOptions> options, ILogger<EcdsaCommandSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Value.CommandSigning;
        _key = Load(settings, logger);
        if (_key.KeySize != 256)
        {
            throw new InvalidOperationException("The command signing key must be an ECDSA P-256 key.");
        }

        var spki = _key.ExportSubjectPublicKeyInfo();
        KeyId = DeriveKeyId(spki);
        var keys = new List<CommandSigningKey> { new() { KeyId = KeyId, PublicKey = Convert.ToBase64String(spki) } };
        foreach (var extra in settings.AdditionalPublicKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            var bytes = Convert.FromBase64String(extra.Trim());
            using var check = ECDsa.Create();
            check.ImportSubjectPublicKeyInfo(bytes, out _);
            keys.Add(new CommandSigningKey { KeyId = DeriveKeyId(bytes), PublicKey = Convert.ToBase64String(bytes) });
        }

        KeySet = new CommandKeySet { Keys = keys };
        LogLoaded(logger, KeyId);
    }

    public string KeyId { get; }

    public CommandKeySet KeySet { get; }

    public SignedCommand Sign(CommandEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(envelope.KeyId, KeyId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The envelope names a different signing key.");
        }

        return CommandSignature.Sign(envelope, _key);
    }

    public void Dispose() => _key.Dispose();

    /// <summary>Stable id: the first 16 hex characters of the SHA-256 of the public key.</summary>
    public static string DeriveKeyId(ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        "cmd-" + Convert.ToHexStringLower(SHA256.HashData(subjectPublicKeyInfo))[..16];

    private static ECDsa Load(CommandSigningOptions settings, ILogger logger)
    {
        var key = ECDsa.Create();
        if (!string.IsNullOrWhiteSpace(settings.KeyPem))
        {
            key.ImportFromPem(settings.KeyPem);
            return key;
        }

        if (string.IsNullOrWhiteSpace(settings.KeyPath))
        {
            throw new InvalidOperationException(
                $"No command signing key is configured. Set {RemoteManagementOptions.SectionName}:CommandSigning:KeyPath or KeyPem (ECDSA P-256 PEM).");
        }

        if (File.Exists(settings.KeyPath))
        {
            key.ImportFromPem(File.ReadAllText(settings.KeyPath));
            return key;
        }

        if (!settings.AutoGenerate)
        {
            throw new InvalidOperationException($"The command signing key file '{settings.KeyPath}' does not exist.");
        }

        key.GenerateKey(ECCurve.NamedCurves.nistP256);
        var directory = Path.GetDirectoryName(Path.GetFullPath(settings.KeyPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var pem = key.ExportPkcs8PrivateKeyPem();
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(settings.KeyPath, pem);
        }
        else
        {
            using var stream = new FileStream(settings.KeyPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(pem);
        }

        LogGenerated(logger, settings.KeyPath);
        return key;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Command signing key {KeyId} loaded")]
    private static partial void LogLoaded(ILogger logger, string keyId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Generated a new command signing key at {Path}. Use a managed key outside development.")]
    private static partial void LogGenerated(ILogger logger, string path);
}
