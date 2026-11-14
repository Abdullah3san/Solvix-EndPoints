namespace Solvix.Agent.Windows.RemoteManagement;

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Solvix.Agent.Core.RemoteManagement.Privileged;

/// <summary>
/// Restart, shutdown, sign-off and lock through documented Win32 APIs. Runs only inside the
/// Solvix Remote Executor service (LocalSystem), never in the unprivileged agent service.
/// Not executed in the development environment (no Windows machine): see docs/PHASE-06-DESIGN.md.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPowerOperations : IPowerOperations
{
    private const uint ShutdownReasonPlanned = 0x80000000; // SHTDN_REASON_FLAG_PLANNED | MAJOR_OTHER | MINOR_OTHER
    private const uint NoActiveSession = 0xFFFFFFFF;
    private const string ShutdownPrivilege = "SeShutdownPrivilege";

    public Task RestartAsync(TimeSpan delay, string? message, CancellationToken cancellationToken) => Initiate(delay, message, reboot: true);

    public Task ShutdownAsync(TimeSpan delay, string? message, CancellationToken cancellationToken) => Initiate(delay, message, reboot: false);

    public Task LogOffAsync(CancellationToken cancellationToken)
    {
        var session = ActiveSession();
        if (!WTSLogoffSession(IntPtr.Zero, (int)session, false))
        {
            throw new PowerOperationException("logoff_failed", new Win32Exception().Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disconnecting the console session puts it on the lock screen and keeps the user's programs
    /// running. (A user-session helper calling LockWorkStation is planned; this needs no helper.)
    /// </summary>
    public Task LockAsync(CancellationToken cancellationToken)
    {
        var session = ActiveSession();
        if (!WTSDisconnectSession(IntPtr.Zero, (int)session, false))
        {
            throw new PowerOperationException("lock_failed", new Win32Exception().Message);
        }

        return Task.CompletedTask;
    }

    private static Task Initiate(TimeSpan delay, string? message, bool reboot)
    {
        EnablePrivilege(ShutdownPrivilege);

        // bForceAppsClosed is false on purpose: an application that refuses (unsaved work) blocks the
        // operation rather than losing data; the technician sees the resulting state in the device status.
        if (!InitiateSystemShutdownExW(null, message, (uint)delay.TotalSeconds, false, reboot, ShutdownReasonPlanned))
        {
            throw new PowerOperationException(reboot ? "restart_failed" : "shutdown_failed", new Win32Exception().Message);
        }

        return Task.CompletedTask;
    }

    private static uint ActiveSession()
    {
        var session = WTSGetActiveConsoleSessionId();
        return session == NoActiveSession
            ? throw new PowerOperationException("no_active_session", "Nobody is signed in at the console.")
            : session;
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process_GetCurrent(), 0x20 | 0x8, out var token))
        {
            throw new PowerOperationException("privilege_failed", new Win32Exception().Message);
        }

        try
        {
            if (!LookupPrivilegeValueW(null, name, out var luid))
            {
                throw new PowerOperationException("privilege_failed", new Win32Exception().Message);
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero) || Marshal.GetLastWin32Error() != 0)
            {
                throw new PowerOperationException("privilege_failed", "The service does not hold SeShutdownPrivilege.");
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static IntPtr Process_GetCurrent() => GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "InitiateSystemShutdownExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitiateSystemShutdownExW(string? machine, string? message, uint timeout, [MarshalAs(UnmanagedType.Bool)] bool forceAppsClosed, [MarshalAs(UnmanagedType.Bool)] bool reboot, uint reason);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, IntPtr previous, IntPtr returnLength);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll")]
    private static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSLogoffSession(IntPtr server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSDisconnectSession(IntPtr server, int sessionId, [MarshalAs(UnmanagedType.Bool)] bool wait);
}

/// <summary>
/// The executor pipe is reachable by SYSTEM, administrators and the Solvix Agent service account
/// only; remote (network) callers are denied explicitly.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SecuredExecutorPipeFactory(string agentServiceAccount = @"NT SERVICE\SolvixAgent") : IExecutorPipeFactory
{
    public NamedPipeServerStream Create(string name)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new NTAccount(agentServiceAccount), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}
