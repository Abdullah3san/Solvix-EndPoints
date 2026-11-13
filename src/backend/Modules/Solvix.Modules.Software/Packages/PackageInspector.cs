namespace Solvix.Modules.Software.Packages;

using System.Buffers.Binary;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Domain;

/// <summary>Why an uploaded file was refused.</summary>
public sealed class PackageValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public static class PackageValidationCodes
{
    public const string UnsupportedType = "package_type_not_allowed";
    public const string ExtensionMismatch = "package_extension_mismatch";
    public const string Empty = "package_empty";
    public const string TooLarge = "package_too_large";
    public const string Malformed = "package_malformed";
    public const string NotAnInstaller = "package_not_an_installer";
    public const string UnsupportedArchitecture = "package_unsupported_architecture";
    public const string SignatureInvalid = "package_signature_invalid";
    public const string SignatureMismatch = "package_signature_mismatch";
    public const string UnsignedNotAllowed = "package_unsigned_not_allowed";
    public const string Duplicate = "package_duplicate";
    public const string Corrupted = "package_corrupted";
}

/// <summary>
/// Reads an uploaded installer and decides what it is. The type is taken from the content, not
/// the name: an MSI must be an OLE compound file whose root carries the Windows Installer
/// package class id; an EXE must be a PE executable image (not a DLL) for x86, x64 or ARM64.
/// Authenticode signatures are extracted and their CMS signature verified; for PE files the
/// signed image digest is also recomputed from the file, so a modified signed executable is
/// refused. Nothing here executes or loads the file.
/// </summary>
public static class PackageInspector
{
    public const int MaxSignatureBytes = 1024 * 1024;

    // {000C1084-0000-0000-C000-000000000046}: Windows Installer package (not .msp patch or .mst transform).
    private static readonly Guid MsiPackageClassId = new("000C1084-0000-0000-C000-000000000046");
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private const string SpcIndirectDataOid = "1.3.6.1.4.1.311.2.1.4";

    public static InstallerType? TypeFromFileName(string fileName) => Path.GetExtension(fileName).ToUpperInvariant() switch
    {
        ".MSI" => InstallerType.Msi,
        ".EXE" => InstallerType.Exe,
        _ => null,
    };

    public static PackageInspection Inspect(string path, string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var declared = TypeFromFileName(fileName)
            ?? throw new PackageValidationException(PackageValidationCodes.UnsupportedType, "Only .msi and .exe installers can be uploaded.");
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = RandomAccess.GetLength(handle);
        if (length == 0)
        {
            throw new PackageValidationException(PackageValidationCodes.Empty, "The file is empty.");
        }

        var head = new byte[Math.Min(512, length)];
        RandomAccess.Read(handle, head, 0);
        InstallerType actual;
        if (head.AsSpan().StartsWith(CompoundFileSignature))
        {
            actual = InstallerType.Msi;
        }
        else if (head.Length >= 2 && head[0] == (byte)'M' && head[1] == (byte)'Z')
        {
            actual = InstallerType.Exe;
        }
        else
        {
            throw new PackageValidationException(PackageValidationCodes.Malformed, "The file is neither a Windows Installer package nor a Windows executable.");
        }

        if (actual != declared)
        {
            throw new PackageValidationException(PackageValidationCodes.ExtensionMismatch, $"The file extension says {declared} but the content is {(actual == InstallerType.Msi ? "an MSI package" : "an executable")}.");
        }

        try
        {
            return actual == InstallerType.Msi ? InspectMsi(handle, length) : InspectPe(handle, length);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or OverflowException or AsnContentException or InvalidDataException)
        {
            throw new PackageValidationException(PackageValidationCodes.Malformed, "The package structure is damaged or not supported.");
        }
    }

    private static PackageInspection InspectMsi(SafeFileHandle handle, long length)
    {
        var file = CompoundFile.Open(handle, length);
        if (file.RootClassId != MsiPackageClassId)
        {
            throw new PackageValidationException(PackageValidationCodes.NotAnInstaller, "The compound file is not a Windows Installer package (patches and transforms cannot be deployed on their own).");
        }

        var signature = file.ReadStream("\u0005DigitalSignature", MaxSignatureBytes);
        if (signature is null)
        {
            return new PackageInspection(InstallerType.Msi, false, null, null, false, null);
        }

        var (cms, _) = DecodeAuthenticode(signature);
        var signer = Signer(cms);
        return new PackageInspection(InstallerType.Msi, true, signer.Subject, signer.Thumbprint, false, null);
    }

    private static PackageInspection InspectPe(SafeFileHandle handle, long length)
    {
        var pe = PeImage.Read(handle, length);
        if (!pe.IsExecutable || pe.IsDll)
        {
            throw new PackageValidationException(PackageValidationCodes.NotAnInstaller, "The file is not an executable program (DLLs and drivers cannot be deployed).");
        }

        if (pe.Architecture is null)
        {
            throw new PackageValidationException(PackageValidationCodes.UnsupportedArchitecture, "Only x86, x64 and ARM64 executables are supported.");
        }

        if (pe.CertificateTableSize == 0)
        {
            return new PackageInspection(InstallerType.Exe, false, null, null, false, pe.Architecture);
        }

        var blob = pe.ReadFirstCertificate(handle);
        var (cms, digest) = DecodeAuthenticode(blob);
        var actual = pe.ComputeAuthenticodeHash(handle, digest.Algorithm);
        if (!CryptographicOperations.FixedTimeEquals(actual, digest.Value))
        {
            throw new PackageValidationException(PackageValidationCodes.SignatureMismatch, "The executable was modified after it was signed.");
        }

        var signer = Signer(cms);
        return new PackageInspection(InstallerType.Exe, true, signer.Subject, signer.Thumbprint, true, pe.Architecture);
    }

    /// <summary>Decodes a PKCS#7 Authenticode signature, verifies its CMS signature and returns the signed image digest.</summary>
    internal static (SignedCms Cms, (HashAlgorithmName Algorithm, byte[] Value) Digest) DecodeAuthenticode(byte[] pkcs7)
    {
        var cms = new SignedCms();
        try
        {
            cms.Decode(pkcs7);
            cms.CheckSignature(verifySignatureOnly: true);
        }
        catch (CryptographicException)
        {
            throw new PackageValidationException(PackageValidationCodes.SignatureInvalid, "The package carries a signature that is damaged or does not verify.");
        }

        if (cms.ContentInfo.ContentType.Value != SpcIndirectDataOid || cms.SignerInfos.Count == 0 || cms.SignerInfos[0].Certificate is null)
        {
            throw new PackageValidationException(PackageValidationCodes.SignatureInvalid, "The package signature is not an Authenticode signature.");
        }

        return (cms, ReadIndirectDigest(cms.ContentInfo.Content));
    }

    /// <summary>SpcIndirectDataContent ::= SEQUENCE { data SpcAttributeTypeAndOptionalValue, messageDigest DigestInfo }.</summary>
    internal static (HashAlgorithmName Algorithm, byte[] Value) ReadIndirectDigest(ReadOnlyMemory<byte> content)
    {
        var reader = new AsnReader(content, AsnEncodingRules.BER);
        if (reader.PeekTag().HasSameClassAndValue(Asn1Tag.PrimitiveOctetString))
        {
            reader = new AsnReader(reader.ReadOctetString(), AsnEncodingRules.BER);
        }

        var indirect = reader.ReadSequence();
        indirect.ReadSequence(); // data: type + value, not needed
        var digestInfo = indirect.ReadSequence();
        var algorithm = digestInfo.ReadSequence();
        var oid = algorithm.ReadObjectIdentifier();
        var digest = digestInfo.ReadOctetString();
        var name = oid switch
        {
            "1.3.14.3.2.26" => HashAlgorithmName.SHA1,
            "2.16.840.1.101.3.4.2.1" => HashAlgorithmName.SHA256,
            "2.16.840.1.101.3.4.2.2" => HashAlgorithmName.SHA384,
            "2.16.840.1.101.3.4.2.3" => HashAlgorithmName.SHA512,
            _ => throw new PackageValidationException(PackageValidationCodes.SignatureInvalid, "The signature uses an unsupported digest algorithm."),
        };
        return (name, digest);
    }

    private static (string Subject, string Thumbprint) Signer(SignedCms cms)
    {
        var certificate = cms.SignerInfos[0].Certificate!;
        return (certificate.Subject, certificate.GetCertHashString(HashAlgorithmName.SHA256).ToUpperInvariant());
    }
}

/// <summary>Minimal, bounds-checked PE header reader (enough for type checks and the Authenticode digest).</summary>
internal sealed class PeImage
{
    private const ushort ExecutableImage = 0x0002;
    private const ushort Dll = 0x2000;

    private PeImage()
    {
    }

    public long Length { get; private init; }

    public bool IsExecutable { get; private init; }

    public bool IsDll { get; private init; }

    public string? Architecture { get; private init; }

    public long ChecksumOffset { get; private init; }

    public long CertificateEntryOffset { get; private init; }

    public long CertificateTableOffset { get; private init; }

    public long CertificateTableSize { get; private init; }

    public static PeImage Read(SafeFileHandle handle, long length)
    {
        var dos = ReadExactly(handle, 0, 64, length);
        var peOffset = BinaryPrimitives.ReadInt32LittleEndian(dos.AsSpan(0x3C));
        if (peOffset < 64 || peOffset > 1024 * 1024 || peOffset + 24 > length)
        {
            throw new InvalidDataException("Invalid PE header offset.");
        }

        var header = ReadExactly(handle, peOffset, 24, length);
        if (!header.AsSpan(0, 4).SequenceEqual("PE\0\0"u8))
        {
            throw new InvalidDataException("Missing PE signature.");
        }

        var machine = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20));
        var characteristics = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22));
        var optionalOffset = peOffset + 24L;
        if (optionalSize < 96 || optionalOffset + optionalSize > length)
        {
            throw new InvalidDataException("Invalid optional header.");
        }

        var optional = ReadExactly(handle, optionalOffset, optionalSize, length);
        var magic = BinaryPrimitives.ReadUInt16LittleEndian(optional);
        var (directoryCountOffset, directoriesOffset) = magic switch
        {
            0x10B => (92, 96),
            0x20B => (108, 112),
            _ => throw new InvalidDataException("Unknown optional header magic."),
        };
        if (directoriesOffset > optionalSize)
        {
            throw new InvalidDataException("Truncated optional header.");
        }

        var directories = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(directoryCountOffset));
        long tableOffset = 0;
        long tableSize = 0;
        var entryOffset = directoriesOffset + (4 * 8);
        if (directories > 4 && entryOffset + 8 <= optionalSize)
        {
            tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(entryOffset));
            tableSize = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(entryOffset + 4));
            if (tableSize > 0 && (tableOffset < optionalOffset + optionalSize || tableOffset + tableSize > length || tableSize < 8))
            {
                throw new InvalidDataException("Invalid certificate table.");
            }
        }

        return new PeImage
        {
            Length = length,
            IsExecutable = (characteristics & ExecutableImage) != 0,
            IsDll = (characteristics & Dll) != 0,
            Architecture = machine switch
            {
                0x014C => "x86",
                0x8664 => "x64",
                0xAA64 => "arm64",
                _ => null,
            },
            ChecksumOffset = optionalOffset + 64,
            CertificateEntryOffset = optionalOffset + entryOffset,
            CertificateTableOffset = tableSize > 0 ? tableOffset : 0,
            CertificateTableSize = tableSize,
        };
    }

    /// <summary>The PKCS#7 blob of the first WIN_CERTIFICATE (revision 2.0, PKCS signed data).</summary>
    public byte[] ReadFirstCertificate(SafeFileHandle handle)
    {
        var header = ReadExactly(handle, CertificateTableOffset, 8, Length);
        var entryLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
        var revision = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if (revision != 0x0200 || type != 0x0002 || entryLength <= 8 || entryLength > CertificateTableSize || entryLength - 8 > PackageInspector.MaxSignatureBytes)
        {
            throw new PackageValidationException(PackageValidationCodes.SignatureInvalid, "The executable's certificate table is not a valid Authenticode signature.");
        }

        return ReadExactly(handle, CertificateTableOffset + 8, (int)(entryLength - 8), Length);
    }

    /// <summary>
    /// Authenticode PE image hash: the whole file except the checksum field, the certificate table
    /// directory entry and the certificate table itself (which is at the end of the file).
    /// </summary>
    public byte[] ComputeAuthenticodeHash(SafeFileHandle handle, HashAlgorithmName algorithm)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        var end = CertificateTableSize > 0 ? CertificateTableOffset : Length;
        Append(hash, handle, 0, ChecksumOffset);
        Append(hash, handle, ChecksumOffset + 4, CertificateEntryOffset);
        Append(hash, handle, CertificateEntryOffset + 8, end);
        return hash.GetHashAndReset();
    }

    private static void Append(IncrementalHash hash, SafeFileHandle handle, long from, long to)
    {
        var buffer = new byte[81920];
        var position = from;
        while (position < to)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, to - position)), position);
            if (read <= 0)
            {
                throw new InvalidDataException("Unexpected end of file.");
            }

            hash.AppendData(buffer, 0, read);
            position += read;
        }
    }

    internal static byte[] ReadExactly(SafeFileHandle handle, long offset, int count, long length)
    {
        if (offset < 0 || count < 0 || offset + count > length)
        {
            throw new InvalidDataException("Read outside the file.");
        }

        var buffer = new byte[count];
        var done = 0;
        while (done < count)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(done), offset + done);
            if (read <= 0)
            {
                throw new InvalidDataException("Unexpected end of file.");
            }

            done += read;
        }

        return buffer;
    }
}

/// <summary>
/// Minimal, bounds-checked reader for OLE compound files ([MS-CFB]), the container format of MSI
/// packages: header, FAT/DIFAT, directory, mini stream. Every chain walk is limited by the
/// number of sectors so a crafted file cannot loop.
/// </summary>
internal sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int MaxDirectoryEntries = 100_000;

    private readonly SafeFileHandle _handle;
    private readonly long _length;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniStreamCutoff;
    private readonly uint[] _fat;
    private readonly List<DirectoryEntry> _entries;
    private uint[]? _miniFat;
    private byte[]? _miniStream;
    private readonly uint _firstMiniFatSector;

    private CompoundFile(SafeFileHandle handle, long length, byte[] header)
    {
        _handle = handle;
        _length = length;
        var major = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26));
        var byteOrder = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28));
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30));
        var miniShift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
        if (byteOrder != 0xFFFE || !((major == 3 && sectorShift == 9) || (major == 4 && sectorShift == 12)) || miniShift != 6)
        {
            throw new InvalidDataException("Unsupported compound file header.");
        }

        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        var fatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(44));
        var firstDirectory = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(48));
        _miniStreamCutoff = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(56));
        _firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(60));
        var firstDifat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(68));
        var difatSectors = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(72));
        var maxSectors = (length / _sectorSize) + 1;
        if (fatSectors > maxSectors || difatSectors > maxSectors || _miniStreamCutoff != 4096)
        {
            throw new InvalidDataException("Implausible compound file header.");
        }

        // FAT sector numbers: 109 in the header, the rest in the DIFAT chain.
        var fatSectorNumbers = new List<uint>();
        for (var i = 0; i < 109 && fatSectorNumbers.Count < fatSectors; i++)
        {
            fatSectorNumbers.Add(BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(76 + (i * 4))));
        }

        var difat = firstDifat;
        for (var walked = 0; fatSectorNumbers.Count < fatSectors && difat != EndOfChain && difat != FreeSector; walked++)
        {
            if (walked > difatSectors)
            {
                throw new InvalidDataException("DIFAT chain too long.");
            }

            var sector = ReadSector(difat);
            var perSector = (_sectorSize / 4) - 1;
            for (var i = 0; i < perSector && fatSectorNumbers.Count < fatSectors; i++)
            {
                fatSectorNumbers.Add(BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4)));
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(perSector * 4));
        }

        _fat = new uint[fatSectorNumbers.Count * (_sectorSize / 4)];
        for (var i = 0; i < fatSectorNumbers.Count; i++)
        {
            var sector = ReadSector(fatSectorNumbers[i]);
            for (var j = 0; j < _sectorSize / 4; j++)
            {
                _fat[(i * (_sectorSize / 4)) + j] = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(j * 4));
            }
        }

        var directory = ReadChain(firstDirectory, long.MaxValue, MaxDirectoryEntries * 128L);
        _entries = [];
        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var raw = directory.AsSpan(offset, 128);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(raw[64..]);
            var type = raw[66];
            var name = nameLength is >= 2 and <= 64 ? Encoding.Unicode.GetString(raw[..(nameLength - 2)]) : string.Empty;
            var size = major == 3 ? BinaryPrimitives.ReadUInt32LittleEndian(raw[120..]) : (long)BinaryPrimitives.ReadUInt64LittleEndian(raw[120..]);
            _entries.Add(new DirectoryEntry(name, type, new Guid(raw.Slice(80, 16)), BinaryPrimitives.ReadUInt32LittleEndian(raw[116..]), size));
        }

        if (_entries.Count == 0 || _entries[0].Type != 5)
        {
            throw new InvalidDataException("Missing root entry.");
        }
    }

    public Guid RootClassId => _entries[0].ClassId;

    public static CompoundFile Open(SafeFileHandle handle, long length)
    {
        if (length < 1024)
        {
            throw new InvalidDataException("Too small for a compound file.");
        }

        return new CompoundFile(handle, length, PeImage.ReadExactly(handle, 0, 512, length));
    }

    /// <summary>The content of the first stream called <paramref name="name"/>, or null when there is none.</summary>
    public byte[]? ReadStream(string name, int maxBytes)
    {
        var entry = _entries.FirstOrDefault(e => e.Type == 2 && e.Name == name);
        if (entry is null)
        {
            return null;
        }

        if (entry.Size > maxBytes || entry.Size < 0)
        {
            throw new InvalidDataException("Stream too large.");
        }

        return entry.Size < _miniStreamCutoff ? ReadMini(entry.Start, (int)entry.Size) : ReadChain(entry.Start, entry.Size, maxBytes);
    }

    private byte[] ReadMini(uint start, int size)
    {
        if (_miniStream is null)
        {
            var root = _entries[0];
            _miniStream = ReadChain(root.Start, root.Size, 64L * 1024 * 1024);
            var miniFatBytes = ReadChain(_firstMiniFatSector, long.MaxValue, 16L * 1024 * 1024);
            _miniFat = new uint[miniFatBytes.Length / 4];
            for (var i = 0; i < _miniFat.Length; i++)
            {
                _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));
            }
        }

        var result = new byte[size];
        var visited = new HashSet<uint>();
        var sector = start;
        var done = 0;
        while (done < size)
        {
            if (sector >= _miniFat!.Length || !visited.Add(sector))
            {
                throw new InvalidDataException("Broken mini stream chain.");
            }

            var offset = (long)sector * _miniSectorSize;
            var count = Math.Min(_miniSectorSize, size - done);
            if (offset + count > _miniStream.Length)
            {
                throw new InvalidDataException("Mini sector outside the mini stream.");
            }

            Array.Copy(_miniStream, offset, result, done, count);
            done += count;
            sector = _miniFat[sector];
        }

        return result;
    }

    private byte[] ReadChain(uint start, long size, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var visited = new HashSet<uint>();
        var sector = start;
        while (sector != EndOfChain && buffer.Length < size)
        {
            if (sector >= _fat.Length || !visited.Add(sector))
            {
                throw new InvalidDataException("Broken or cyclic sector chain.");
            }

            var data = ReadSector(sector);
            buffer.Write(data, 0, (int)Math.Min(data.Length, size - buffer.Length));
            if (buffer.Length > maxBytes)
            {
                throw new InvalidDataException("Chain too long.");
            }

            sector = _fat[sector];
        }

        if (size != long.MaxValue && buffer.Length < size)
        {
            throw new InvalidDataException("Truncated stream.");
        }

        return buffer.ToArray();
    }

    private byte[] ReadSector(uint sector)
    {
        var offset = ((long)sector + 1) * _sectorSize;
        if (sector >= 0xFFFFFFFA || offset >= _length)
        {
            throw new InvalidDataException("Sector outside the file.");
        }

        // The last sector of a file may be short.
        var count = (int)Math.Min(_sectorSize, _length - offset);
        var data = PeImage.ReadExactly(_handle, offset, count, _length);
        return count == _sectorSize ? data : [.. data, .. new byte[_sectorSize - count]];
    }

    private sealed record DirectoryEntry(string Name, byte Type, Guid ClassId, uint Start, long Size);
}
