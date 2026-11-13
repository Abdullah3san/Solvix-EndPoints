namespace Solvix.UnitTests.Software;

using System.Security.Cryptography;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Domain;
using Solvix.Modules.Software.Packages;

/// <summary>Package validation: type from content, structural checks, Authenticode extraction and integrity.</summary>
public sealed class PackageValidationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "solvix-package-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void An_unsigned_executable_is_accepted_and_described()
    {
        var inspection = Inspect("setup.exe", TestPackages.Executable());
        Assert.Equal(InstallerType.Exe, inspection.InstallerType);
        Assert.False(inspection.Signed);
        Assert.Equal("x64", inspection.Architecture);
    }

    [Fact]
    public void A_signed_executable_has_its_signer_and_digest_verified()
    {
        using var certificate = TestPackages.Certificate();
        var inspection = Inspect("setup.exe", TestPackages.Sign(TestPackages.Executable(), certificate));

        Assert.True(inspection.Signed);
        Assert.True(inspection.ContentDigestVerified);
        Assert.Equal("CN=Contoso Code Signing", inspection.SignerSubject);
        Assert.Equal(certificate.GetCertHashString(HashAlgorithmName.SHA256), inspection.SignerThumbprint);
    }

    [Fact]
    public void A_signed_executable_modified_after_signing_is_rejected()
    {
        using var certificate = TestPackages.Certificate();
        var signed = TestPackages.Sign(TestPackages.Executable(), certificate);
        signed[TestPackages.PeOffset + 400] ^= 0x01; // one bit of the image

        Assert.Equal(PackageValidationCodes.SignatureMismatch, Reject("setup.exe", signed));
    }

    [Fact]
    public void A_damaged_signature_is_rejected()
    {
        using var certificate = TestPackages.Certificate();
        var signed = TestPackages.Sign(TestPackages.Executable(), certificate);
        signed[^40] ^= 0xFF; // inside the PKCS#7 signature value

        Assert.Equal(PackageValidationCodes.SignatureInvalid, Reject("setup.exe", signed));
    }

    [Fact]
    public void The_digest_code_matches_real_microsoft_signed_binaries()
    {
        // Microsoft signs the assemblies it ships on NuGet with Authenticode.
        var path = typeof(Microsoft.EntityFrameworkCore.DbContext).Assembly.Location;
        using var handle = File.OpenHandle(path);
        var length = RandomAccess.GetLength(handle);
        var image = PeImage.Read(handle, length);
        Assert.True(image.CertificateTableSize > 0, "The test expects a signed binary.");

        var (cms, digest) = PackageInspector.DecodeAuthenticode(image.ReadFirstCertificate(handle));
        Assert.Contains("Microsoft", cms.SignerInfos[0].Certificate!.Subject, StringComparison.Ordinal);
        Assert.Equal(digest.Value, image.ComputeAuthenticodeHash(handle, digest.Algorithm));
    }

    [Theory]
    [InlineData((ushort)0x8664, "x64")]
    [InlineData((ushort)0x014C, "x86")]
    [InlineData((ushort)0xAA64, "arm64")]
    public void Supported_architectures_are_recognised(ushort machine, string architecture) =>
        Assert.Equal(architecture, Inspect("setup.exe", TestPackages.Executable(machine)).Architecture);

    [Fact]
    public void Libraries_unknown_architectures_and_garbage_are_rejected()
    {
        Assert.Equal(PackageValidationCodes.NotAnInstaller, Reject("setup.exe", TestPackages.Executable(dll: true)));
        Assert.Equal(PackageValidationCodes.UnsupportedArchitecture, Reject("setup.exe", TestPackages.Executable(machine: 0x01C4)));
        Assert.Equal(PackageValidationCodes.Malformed, Reject("setup.exe", [(byte)'M', (byte)'Z', 1, 2, 3]));
        Assert.Equal(PackageValidationCodes.Malformed, Reject("setup.exe", "#!/bin/sh\nrm -rf /\n"u8.ToArray()));
        Assert.Equal(PackageValidationCodes.Empty, Reject("setup.exe", []));
    }

    [Fact]
    public void The_type_comes_from_the_content_and_must_match_the_extension()
    {
        Assert.Equal(PackageValidationCodes.ExtensionMismatch, Reject("setup.msi", TestPackages.Executable()));
        Assert.Equal(PackageValidationCodes.ExtensionMismatch, Reject("package.exe", TestPackages.CompoundFile()));
        Assert.Equal(PackageValidationCodes.UnsupportedType, Reject("script.ps1", TestPackages.Executable()));
        Assert.Equal(PackageValidationCodes.UnsupportedType, Reject("setup.exe.zip", TestPackages.Executable()));
        Assert.Equal(PackageValidationCodes.UnsupportedType, Reject("payload.msp", TestPackages.CompoundFile()));
    }

    [Fact]
    public void An_unsigned_msi_is_accepted()
    {
        var inspection = Inspect("notes.msi", TestPackages.CompoundFile(null, ("\u0005SummaryInformation", new byte[200])));
        Assert.Equal(InstallerType.Msi, inspection.InstallerType);
        Assert.False(inspection.Signed);
    }

    [Fact]
    public void Msi_signatures_are_read_from_the_mini_stream_and_from_regular_sectors()
    {
        using var certificate = TestPackages.Certificate("CN=Contoso Installer");
        var small = TestPackages.Pkcs7(certificate);
        Assert.True(small.Length < 4096);
        var inspection = Inspect("notes.msi", TestPackages.CompoundFile(null, ("\u0005DigitalSignature", small)));
        Assert.True(inspection.Signed);
        Assert.Equal("CN=Contoso Installer", inspection.SignerSubject);
        Assert.False(inspection.ContentDigestVerified);

        var extra = Enumerable.Range(0, 6).Select(i => TestPackages.Certificate($"CN=Intermediate {i}")).ToArray();
        try
        {
            var large = TestPackages.Pkcs7(certificate, extra);
            Assert.True(large.Length >= 4096);
            Assert.True(Inspect("notes.msi", TestPackages.CompoundFile(null, ("\u0005DigitalSignature", large))).Signed);
        }
        finally
        {
            foreach (var c in extra)
            {
                c.Dispose();
            }
        }
    }

    [Fact]
    public void Patches_transforms_and_damaged_compound_files_are_rejected()
    {
        // .msp patch class id: not deployable on its own.
        Assert.Equal(PackageValidationCodes.NotAnInstaller, Reject("patch.msi", TestPackages.CompoundFile(new Guid("000C1086-0000-0000-C000-000000000046"))));

        var damaged = TestPackages.CompoundFile(null, ("\u0005DigitalSignature", new byte[100]));
        damaged[30] = 13; // sector shift
        Assert.Equal(PackageValidationCodes.Malformed, Reject("notes.msi", damaged));

        var truncated = TestPackages.CompoundFile()[..600];
        Assert.Equal(PackageValidationCodes.Malformed, Reject("notes.msi", truncated));

        // A signature stream that is not PKCS#7.
        Assert.Equal(PackageValidationCodes.SignatureInvalid, Reject("notes.msi", TestPackages.CompoundFile(null, ("\u0005DigitalSignature", RandomNumberGenerator.GetBytes(300)))));
    }

    [Fact]
    public void A_self_referencing_sector_chain_cannot_loop()
    {
        var file = TestPackages.CompoundFile(null, ("\u0005DigitalSignature", new byte[5000]));

        // Make the first data sector of the regular stream point to itself in the FAT.
        var fatOffset = 512;
        var firstDataSector = BitConverter.ToUInt32(file, 512 + 512 + 128 + 116);
        BitConverter.GetBytes(firstDataSector).CopyTo(file, fatOffset + ((int)firstDataSector * 4));
        Assert.Equal(PackageValidationCodes.Malformed, Reject("notes.msi", file));
    }

    [Theory]
    [InlineData(@"C:\Users\x\Downloads\Notes Setup.msi", "Notes Setup.msi")]
    [InlineData("../../etc/passwd.exe", "passwd.exe")]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("a<b>c|d\"e.msi", "abcde.msi")]
    [InlineData(".hidden.exe", "hidden.exe")]
    public void Uploaded_file_names_are_reduced_to_a_display_name(string input, string expected) =>
        Assert.Equal(expected, PackageService.SanitizeFileName(input));

    private PackageInspection Inspect(string name, byte[] content) => PackageInspector.Inspect(TestPackages.Write(_directory, name, content), name);

    private string Reject(string name, byte[] content) =>
        Assert.Throws<PackageValidationException>(() => Inspect(name, content)).Code;
}
