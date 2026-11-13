namespace Solvix.UnitTests.Software;

using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;

/// <summary>Builds minimal but structurally valid PE executables and MSI compound files for validation tests.</summary>
internal static class TestPackages
{
    public const int PeOffset = 0x80;

    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint Free = 0xFFFFFFFF;
    private static readonly Guid MsiClassId = new("000C1084-0000-0000-C000-000000000046");

    /// <summary>A PE32+ image with no sections, padded with <paramref name="bodyBytes"/> bytes of content.</summary>
    public static byte[] Executable(ushort machine = 0x8664, bool dll = false, int bodyBytes = 4096)
    {
        const int optionalSize = 240; // PE32+ standard fields + 16 data directories
        var file = new byte[PeOffset + 24 + optionalSize + bodyBytes];
        file[0] = (byte)'M';
        file[1] = (byte)'Z';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(0x3C), PeOffset);
        "PE\0\0"u8.CopyTo(file.AsSpan(PeOffset));
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(PeOffset + 4), machine);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(PeOffset + 20), optionalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(PeOffset + 22), (ushort)(0x0022 | (dll ? 0x2000 : 0)));
        var optional = PeOffset + 24;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(optional), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(optional + 108), 16);
        for (var i = optional + optionalSize; i < file.Length; i++)
        {
            file[i] = (byte)(i * 31 % 251);
        }

        return file;
    }

    /// <summary>Authenticode-signs a PE built by <see cref="Executable"/>: digest, SpcIndirectDataContent, CMS, certificate table.</summary>
    public static byte[] Sign(byte[] executable, X509Certificate2 signer)
    {
        var optional = PeOffset + 24;
        var checksum = optional + 64;
        var entry = optional + 112 + (4 * 8);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(executable, 0, checksum);
        hash.AppendData(executable, checksum + 4, entry - checksum - 4);
        hash.AppendData(executable, entry + 8, executable.Length - entry - 8);
        var digest = hash.GetHashAndReset();

        var cms = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), IndirectData(digest)), detached: false);
        cms.ComputeSignature(new CmsSigner(signer) { IncludeOption = X509IncludeOption.EndCertOnly });
        var pkcs7 = cms.Encode();
        var length = 8 + pkcs7.Length;
        var padded = (length + 7) / 8 * 8;
        var signed = new byte[executable.Length + padded];
        executable.CopyTo(signed, 0);
        var table = executable.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(table), (uint)length);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(table + 4), 0x0200);
        BinaryPrimitives.WriteUInt16LittleEndian(signed.AsSpan(table + 6), 0x0002);
        pkcs7.CopyTo(signed, table + 8);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(entry), (uint)table);
        BinaryPrimitives.WriteUInt32LittleEndian(signed.AsSpan(entry + 4), (uint)padded);
        return signed;
    }

    /// <summary>A PKCS#7 Authenticode blob over an arbitrary digest (MSI signatures are not digest-checked by the server).</summary>
    public static byte[] Pkcs7(X509Certificate2 signer, params X509Certificate2[] extra)
    {
        var cms = new SignedCms(new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), IndirectData(new byte[32])), detached: false);
        var cmsSigner = new CmsSigner(signer) { IncludeOption = X509IncludeOption.EndCertOnly };
        foreach (var certificate in extra)
        {
            cmsSigner.Certificates.Add(certificate);
        }

        cms.ComputeSignature(cmsSigner);
        return cms.Encode();
    }

    public static X509Certificate2 Certificate(string subject = "CN=Contoso Code Signing")
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    /// <summary>
    /// A version 3 compound file (512-byte sectors): FAT, directory, mini FAT, mini stream, then regular
    /// streams. Streams below 4096 bytes go into the mini stream, as Windows Installer writes them.
    /// </summary>
    public static byte[] CompoundFile(Guid? rootClassId = null, params (string Name, byte[] Data)[] streams)
    {
        const int sector = 512;
        var sectors = new List<byte[]>();
        var fat = new List<uint>();

        // 0: FAT, 1: directory, 2: mini FAT (filled in below).
        sectors.Add(new byte[sector]);
        fat.Add(0xFFFFFFFD);
        sectors.Add(new byte[sector]);
        fat.Add(EndOfChain);
        sectors.Add(new byte[sector]);
        fat.Add(EndOfChain);

        uint Append(byte[] data)
        {
            var start = (uint)sectors.Count;
            for (var offset = 0; offset < Math.Max(1, data.Length); offset += sector)
            {
                var chunk = new byte[sector];
                Array.Copy(data, offset, chunk, 0, Math.Min(sector, data.Length - offset));
                sectors.Add(chunk);
                fat.Add((uint)sectors.Count);
            }

            fat[^1] = EndOfChain;
            return start;
        }

        // Mini stream.
        var mini = new MemoryStream();
        var miniFat = new List<uint>();
        var entries = new List<(string Name, byte Type, uint Start, uint Size)>();
        foreach (var (name, data) in streams.Where(s => s.Data.Length < 4096))
        {
            var start = (uint)miniFat.Count;
            var count = Math.Max(1, (data.Length + 63) / 64);
            for (var i = 0; i < count; i++)
            {
                miniFat.Add(i == count - 1 ? EndOfChain : start + (uint)i + 1);
            }

            mini.Write(data);
            mini.Write(new byte[(count * 64) - data.Length]);
            entries.Add((name, 2, start, (uint)data.Length));
        }

        var miniStart = mini.Length > 0 ? Append(mini.ToArray()) : EndOfChain;
        foreach (var (name, data) in streams.Where(s => s.Data.Length >= 4096))
        {
            entries.Add((name, 2, Append(data), (uint)data.Length));
        }

        for (var i = 0; i < sector / 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sectors[2].AsSpan(i * 4), i < miniFat.Count ? miniFat[i] : Free);
        }

        // Directory: root + streams (at most three in one sector, enough for the tests).
        var directory = sectors[1];
        WriteEntry(directory, 0, "Root Entry", 5, rootClassId ?? MsiClassId, miniStart, (uint)mini.Length, child: entries.Count > 0 ? 1u : Free);
        for (var i = 0; i < entries.Count; i++)
        {
            WriteEntry(directory, i + 1, entries[i].Name, entries[i].Type, Guid.Empty, entries[i].Start, entries[i].Size, child: Free, right: i + 1 < entries.Count ? (uint)(i + 2) : Free);
        }

        for (var i = 0; i < sector / 4; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sectors[0].AsSpan(i * 4), i < fat.Count ? fat[i] : Free);
        }

        var header = new byte[sector];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68), EndOfChain);
        for (var i = 0; i < 109; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + (i * 4)), i == 0 ? 0 : Free);
        }

        return [.. header, .. sectors.SelectMany(s => s)];
    }

    public static string Write(string directory, string name, byte[] content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] IndirectData(byte[] digest)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.4.1.311.2.1.15");
            }

            using (writer.PushSequence())
            {
                using (writer.PushSequence())
                {
                    writer.WriteObjectIdentifier("2.16.840.1.101.3.4.2.1");
                    writer.WriteNull();
                }

                writer.WriteOctetString(digest);
            }
        }

        return writer.Encode();
    }

    private static void WriteEntry(byte[] sector, int index, string name, byte type, Guid classId, uint start, uint size, uint child, uint right = 0xFFFFFFFF)
    {
        var entry = sector.AsSpan(index * 128, 128);
        var encoded = Encoding.Unicode.GetBytes(name);
        encoded.CopyTo(entry);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[64..], (ushort)(encoded.Length + 2));
        entry[66] = type;
        entry[67] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(entry[68..], Free);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[72..], right);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[76..], child);
        classId.TryWriteBytes(entry.Slice(80, 16));
        BinaryPrimitives.WriteUInt32LittleEndian(entry[116..], start);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[120..], size);
    }
}
