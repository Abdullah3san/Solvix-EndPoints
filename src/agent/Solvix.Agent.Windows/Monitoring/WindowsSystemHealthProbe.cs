namespace Solvix.Agent.Windows.Monitoring;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Solvix.Agent.Core.Monitoring;

/// <summary>Native memory status and the well-known Windows "restart required" markers.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSystemHealthProbe(TimeProvider time) : PortableSystemHealthProbe(time)
{
    private static readonly string[] RebootKeys =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending",
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
    ];

    protected override (long? Total, long? Available) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)status.TotalPhys, (long)status.AvailPhys)
            : base.ReadMemory();
    }

    protected override bool? ReadRebootPending()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var path in RebootKeys)
            {
                using var key = hklm.OpenSubKey(path);
                if (key is not null)
                {
                    return true;
                }
            }

            using var session = hklm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            return session?.GetValue("PendingFileRenameOperations") is string[] { Length: > 0 };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
