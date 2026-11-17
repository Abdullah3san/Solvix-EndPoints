namespace Solvix.Agent.Windows.Remote;

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Solvix.Agent.Core.Remote;

/// <summary>
/// Accepts a screen helper only when it runs in the session of the person at the console, never in
/// session 0 (services) and never in another user's or a remote session.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsHelperAuthorizer : IHelperAuthorizer
{
    private const uint NoSession = 0xFFFFFFFF;

    public bool IsAuthorized(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        var console = WTSGetActiveConsoleSessionId();
        if (console == NoSession || console == 0)
        {
            return false;
        }

        return GetNamedPipeClientSessionId(pipe.SafePipeHandle, out var client) && client == console;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint sessionId);
}
