namespace Solvix.Agent.Windows.Remote;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Solvix.Agent.Core.Remote;

/// <summary>
/// Shows a Yes/No dialog on the screen of the person signed in at the console, from the service
/// (session 0), using <c>WTSSendMessage</c>. No answer, no signed-in user, or any failure means no.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WtsConsentPrompter : IConsentPrompter
{
    private const int MbYesNo = 0x4;
    private const int MbIconQuestion = 0x20;
    private const int MbSetForeground = 0x10000;
    private const int MbTopMost = 0x40000;
    private const int IdYes = 6;
    private const uint NoSession = 0xFFFFFFFF;

    public Task<bool> AskAsync(string requestedBy, string? reason, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() => Ask(requestedBy, reason, timeout), cancellationToken);

    private static bool Ask(string requestedBy, string? reason, TimeSpan timeout)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            return false;
        }

        var title = "Solvix IT support";
        var message = string.IsNullOrWhiteSpace(reason)
            ? $"{requestedBy} from IT would like to open a remote command session on this computer.\n\nAllow it?"
            : $"{requestedBy} from IT would like to open a remote command session on this computer.\n\nReason: {reason}\n\nAllow it?";
        var ok = WTSSendMessageW(
            IntPtr.Zero, session, title, title.Length * 2, message, message.Length * 2,
            MbYesNo | MbIconQuestion | MbSetForeground | MbTopMost, (int)timeout.TotalSeconds, out var response, true);
        return ok && response == IdYes;
    }

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSSendMessageW(
        IntPtr server, uint sessionId, string title, int titleLength, string message, int messageLength, int style, int timeout, out int response, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
