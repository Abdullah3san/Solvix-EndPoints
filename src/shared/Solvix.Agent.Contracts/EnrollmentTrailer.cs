namespace Solvix.Agent.Contracts;

using System.Text;
using System.Text.Json;

/// <summary>
/// Enrollment data appended to the END of <c>Solvix.Agent.exe</c> by the server when an administrator
/// downloads an installer: <c>[JSON payload][payload length: Int32 LE]["SLVXENRL"]</c>.
/// The agent reads it from its own file, so a single downloaded .exe is enough (no zip, no pasted
/// command). The payload holds only what <see cref="AgentEnrollmentFile"/> holds; for a deployment
/// profile that is a non-secret enrollment code whose requests are held until an administrator approves them.
///
/// The installed copy of the exe is written WITHOUT the trailer (see <see cref="TryRead"/>'s
/// <c>cleanLength</c>), so the code never stays inside the program files.
///
/// Appending data changes the file, so a trailer-carrying exe cannot keep a code signature made before the
/// download. A signed base exe is therefore served as the zip package; see the server documentation.
/// </summary>
public static class EnrollmentTrailer
{
    public const int MaxPayloadBytes = 16 * 1024;

    private const int FooterLength = 4 + 8;
    private static readonly byte[] Magic = "SLVXENRL"u8.ToArray();

    /// <summary>The bytes to append to an exe for <paramref name="file"/>.</summary>
    public static byte[] Build(AgentEnrollmentFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var payload = JsonSerializer.SerializeToUtf8Bytes(file, AgentJsonContext.Default.AgentEnrollmentFile);
        if (payload.Length > MaxPayloadBytes)
        {
            throw new ArgumentException("The enrollment data is too large.", nameof(file));
        }

        var result = new byte[payload.Length + FooterLength];
        payload.CopyTo(result, 0);
        BitConverter.TryWriteBytes(result.AsSpan(payload.Length, 4), payload.Length);
        Magic.CopyTo(result, payload.Length + 4);
        return result;
    }

    /// <summary>Reads the trailer of the executable at <paramref name="path"/>, if it has one.</summary>
    /// <param name="cleanLength">Length of the program without the trailer (the whole file when there is none).</param>
    public static bool TryRead(string path, out AgentEnrollmentFile? file, out long cleanLength)
    {
        file = null;
        cleanLength = 0;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            cleanLength = stream.Length;
            return TryRead(stream, out file, out cleanLength);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool TryRead(Stream stream, out AgentEnrollmentFile? file, out long cleanLength)
    {
        ArgumentNullException.ThrowIfNull(stream);
        file = null;
        cleanLength = stream.Length;
        if (stream.Length < FooterLength)
        {
            return false;
        }

        Span<byte> footer = stackalloc byte[FooterLength];
        stream.Seek(-FooterLength, SeekOrigin.End);
        stream.ReadExactly(footer);
        if (!footer[4..].SequenceEqual(Magic))
        {
            return false;
        }

        var length = BitConverter.ToInt32(footer[..4]);
        if (length is <= 0 or > MaxPayloadBytes || length + FooterLength > stream.Length)
        {
            return false;
        }

        var payload = new byte[length];
        stream.Seek(-(FooterLength + length), SeekOrigin.End);
        stream.ReadExactly(payload);
        try
        {
            file = JsonSerializer.Deserialize(Encoding.UTF8.GetString(payload), AgentJsonContext.Default.AgentEnrollmentFile);
        }
        catch (JsonException)
        {
            return false;
        }

        if (file is null)
        {
            return false;
        }

        cleanLength = stream.Length - FooterLength - length;
        return true;
    }
}
