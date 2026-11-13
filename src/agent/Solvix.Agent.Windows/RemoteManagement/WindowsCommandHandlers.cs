namespace Solvix.Agent.Windows.RemoteManagement;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Core.RemoteManagement;
using Solvix.Agent.Windows.Inventory;

/// <summary>Windows services (name, display name, state, start mode, account) from WMI. Read-only.</summary>
[SupportedOSPlatform("windows")]
public sealed class ServicesHandler : ICommandHandler
{
    public string CommandType => CommandTypes.Services;

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var services = new JsonArray();
        foreach (var row in Wmi.Query(@"root\cimv2", "Win32_Service", "Name", "DisplayName", "State", "StartMode", "StartName", "ProcessId")
                     .OrderBy(r => r.Text("Name"), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            services.Add(new JsonObject
            {
                ["name"] = row.Text("Name"),
                ["displayName"] = row.Text("DisplayName"),
                ["state"] = row.Text("State"),
                ["startMode"] = row.Text("StartMode"),
                ["account"] = row.Text("StartName"),
                ["processId"] = row.Int("ProcessId"),
            });
        }

        return Task.FromResult(CommandHandlerResult.Json(new JsonObject { ["count"] = services.Count, ["services"] = services }));
    }
}

/// <summary>Signed-in users per Windows session (console and remote) from the Terminal Services API. Read-only.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class LoggedInUsersHandler : ICommandHandler
{
    public string CommandType => CommandTypes.LoggedInUsers;

    public Task<CommandHandlerResult> ExecuteAsync(CommandEnvelope envelope, CancellationToken cancellationToken)
    {
        var sessions = new JsonArray();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return Task.FromResult(CommandHandlerResult.Fail("wts_failed", $"WTSEnumerateSessions failed ({Marshal.GetLastPInvokeError()})."));
        }

        try
        {
            var size = Marshal.SizeOf<WtsSessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WtsSessionInfo>(buffer + (i * size));
                var user = Query(info.SessionId, WtsInfoClass.UserName);
                if (string.IsNullOrEmpty(user))
                {
                    continue; // services session, listeners
                }

                sessions.Add(new JsonObject
                {
                    ["sessionId"] = info.SessionId,
                    ["user"] = user,
                    ["domain"] = Query(info.SessionId, WtsInfoClass.DomainName),
                    ["station"] = Marshal.PtrToStringUni(info.WinStationName),
                    ["state"] = info.State.ToString(),
                    ["client"] = Query(info.SessionId, WtsInfoClass.ClientName),
                });
            }
        }
        finally
        {
            WTSFreeMemory(buffer);
        }

        return Task.FromResult(CommandHandlerResult.Json(new JsonObject
        {
            ["count"] = sessions.Count,
            ["sessions"] = sessions,
            ["collectedAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        }));
    }

    private static string? Query(int sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private enum WtsInfoClass
    {
        UserName = 5,
        DomainName = 7,
        ClientName = 10,
    }

    private enum WtsConnectState
    {
        Active,
        Connected,
        ConnectQuery,
        Shadow,
        Disconnected,
        Idle,
        Listen,
        Reset,
        Down,
        Init,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WtsSessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public WtsConnectState State;
    }

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(IntPtr server, int sessionId, WtsInfoClass infoClass, out IntPtr buffer, out int bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(IntPtr memory);
}
