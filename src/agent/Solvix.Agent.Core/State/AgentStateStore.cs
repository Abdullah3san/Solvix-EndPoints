namespace Solvix.Agent.Core.State;

using System.Text.Json;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Configuration;

public interface IAgentStateStore
{
    AgentState Load();

    void Save(AgentState state);
}

/// <summary>
/// JSON file in the data directory, written atomically (temp file + replace)
/// so a power loss never leaves a half-written state behind.
/// </summary>
public sealed class FileAgentStateStore(IOptions<AgentOptions> options) : IAgentStateStore
{
    private readonly Lock _gate = new();

    private string FilePath => Path.Combine(options.Value.ResolveDataDirectory(), "state.json");

    public AgentState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                return new AgentState();
            }

            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AgentStateJsonContext.Default.AgentState) ?? new AgentState();
            }
            catch (JsonException)
            {
                // Corrupt state: keep a copy for diagnostics and start over (the credential files are separate).
                File.Copy(FilePath, FilePath + ".corrupt", overwrite: true);
                return new AgentState();
            }
        }
    }

    public void Save(AgentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(state, AgentStateJsonContext.Default.AgentState));
        }
    }
}

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents) => WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(contents));

    public static void WriteAllBytes(string path, byte[] contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            stream.Write(contents);
            stream.Flush(true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
