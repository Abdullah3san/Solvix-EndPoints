namespace Solvix.Agent.Windows.Remote;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Solvix.Agent.Contracts;
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

    public Task<bool> AskAsync(RemoteConsentRequest request, TimeSpan timeout, CancellationToken cancellationToken) =>
        Task.Run(() => Ask(request, timeout), cancellationToken);

    private static string Describe(RemoteConsentRequest request) => request.Kind switch
    {
        RemoteSessionKind.Screen when request.ControlRequested => "see your screen and control your mouse and keyboard",
        RemoteSessionKind.Screen => "see your screen (view only)",
        RemoteSessionKind.Files => "browse and copy files in the folders set aside for IT",
        _ => "open a remote command session on this computer",
    };

    private static bool Ask(RemoteConsentRequest request, TimeSpan timeout)
    {
        var session = WTSGetActiveConsoleSessionId();
        if (session == NoSession)
        {
            return false;
        }

        var title = "Solvix IT support";
        var action = Describe(request);
        var message = string.IsNullOrWhiteSpace(request.Reason)
            ? $"{request.RequestedBy} from IT would like to {action}.\n\nAllow it?"
            : $"{request.RequestedBy} from IT would like to {action}.\n\nReason: {request.Reason}\n\nAllow it?";
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
