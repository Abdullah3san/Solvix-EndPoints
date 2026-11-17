namespace Solvix.Agent.Core.Installation;

/// <summary>Administrator elevation for the installer (UAC on Windows).</summary>
public interface IElevation
{
    bool IsElevated { get; }

    /// <summary>Starts this program again with the same arguments after a UAC prompt, waits and returns its exit code (1223 = declined).</summary>
    int RelaunchElevated(IReadOnlyList<string> arguments);
}

/// <summary>Non-Windows hosts have no installer; used by tests and development.</summary>
public sealed class NoElevation : IElevation
{
    public bool IsElevated => true;

    public int RelaunchElevated(IReadOnlyList<string> arguments) => throw new PlatformNotSupportedException();
}
