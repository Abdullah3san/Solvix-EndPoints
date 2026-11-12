namespace Solvix.Agent.Windows.Updates;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Solvix.Agent.Core.Updates;

/// <summary>
/// Verifies an MSI with WinVerifyTrust (full chain incl. revocation) and checks
/// that the signing certificate is one of the locally pinned publishers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class AuthenticodeVerifier : IPackageSignatureVerifier
{
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public string? Verify(string packagePath, IReadOnlyCollection<string> trustedPublisherThumbprints)
    {
        ArgumentNullException.ThrowIfNull(trustedPublisherThumbprints);
        if (trustedPublisherThumbprints.Count == 0)
        {
            return "No trusted publisher is configured.";
        }

        var result = WinVerifyTrust(packagePath);
        if (result != 0)
        {
            return $"Authenticode verification failed (0x{result:X8}).";
        }

#pragma warning disable SYSLIB0057 // No X509CertificateLoader equivalent exists for reading the signer of a signed file.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(packagePath));
#pragma warning restore SYSLIB0057
        var sha1 = signer.GetCertHashString();
        var sha256 = signer.GetCertHashString(HashAlgorithmName.SHA256);
        var pins = trustedPublisherThumbprints.Select(p => p.Replace(":", string.Empty, StringComparison.Ordinal).Trim().ToUpperInvariant());
        return pins.Any(p => p == sha1 || p == sha256) ? null : $"Signer '{signer.Subject}' is not a trusted publisher.";
    }

    private static int WinVerifyTrust(string path)
    {
        var pathPtr = Marshal.StringToCoTaskMemUni(path);
        var fileInfo = new WintrustFileInfo { StructSize = (uint)Marshal.SizeOf<WintrustFileInfo>(), FilePath = pathPtr };
        var filePtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WintrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, filePtr, false);
            var data = new WintrustData
            {
                StructSize = (uint)Marshal.SizeOf<WintrustData>(),
                UiChoice = 2, // WTD_UI_NONE
                RevocationChecks = 1, // WTD_REVOKE_WHOLECHAIN
                UnionChoice = 1, // WTD_CHOICE_FILE
                File = filePtr,
                StateAction = 0, // WTD_STATEACTION_IGNORE
                ProvFlags = 0x80, // WTD_REVOCATION_CHECK_CHAIN_EXCLUDE_ROOT
            };
            var action = GenericVerifyV2;
            return NativeWinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.FreeCoTaskMem(filePtr);
            Marshal.FreeCoTaskMem(pathPtr);
        }
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int NativeWinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint StructSize;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint StructSize;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProvFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}

/// <summary>Installs a verified MSI silently with a fixed, locally defined command line.</summary>
[SupportedOSPlatform("windows")]
public sealed class MsiPackageInstaller(string logDirectory) : IPackageInstaller
{
    public async Task<int> InstallAsync(string packagePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/i", packagePath, "/qn", "/norestart", "/l*v", Path.Combine(logDirectory, $"update-{DateTime.UtcNow:yyyyMMddHHmmss}.log") },
        };
        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("msiexec could not be started.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode is 0 or 3010 ? 0 : process.ExitCode;
    }
}
