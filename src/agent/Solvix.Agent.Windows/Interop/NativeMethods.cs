namespace Solvix.Agent.Windows.Interop;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>Service Control Manager (advapi32) and Power Management (powrprof) APIs.</summary>
internal static partial class NativeMethods
{
    public const uint ScManagerConnect = 0x0001;
    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceNoChange = 0xFFFFFFFF;
    public const uint ServiceAutoStart = 0x00000002;
    public const uint ServiceDemandStart = 0x00000003;
    public const uint ServiceDisabled = 0x00000004;
    public const uint ServiceConfigDelayedAutoStartInfo = 3;

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr OpenService(IntPtr scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(IntPtr handle);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref ServiceDelayedAutoStartInfo info);

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceDelayedAutoStartInfo
    {
        public int DelayedAutostart;
    }

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteACValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerWriteDCValueIndex(IntPtr rootPowerKey, in Guid schemeGuid, in Guid subGroupOfPowerSettingsGuid, in Guid powerSettingGuid, uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("kernel32.dll")]
    public static partial IntPtr LocalFree(IntPtr memory);

    public static void ThrowLastError(string operation) =>
        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"{operation} failed");

    public static void Check(uint error, string operation)
    {
        if (error != 0)
        {
            throw new Win32Exception((int)error, $"{operation} failed");
        }
    }
}

/// <summary>Reads and writes values of the active power scheme.</summary>
internal static class PowerApi
{
    public static readonly Guid SubSleep = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    public static readonly Guid StandbyIdle = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
    public static readonly Guid HibernateIdle = new("9D7815A6-7EE4-497E-8888-515A05F02364");
    public static readonly Guid SubVideo = new("7516B95F-F776-4464-8C53-06167F40CC99");
    public static readonly Guid VideoIdle = new("3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E");
    public static readonly Guid SubButtons = new("4F971E89-EEBD-4455-A8DE-9E59040E7347");
    public static readonly Guid LidAction = new("5CA83367-6E45-459F-A27B-476B1D01C936");
    public static readonly Guid SubNone = new("FEA3413E-7E05-4911-9A71-700331F1C294");
    public static readonly Guid ConsoleLock = new("0E796BDB-100D-47D6-A2D5-F7D2DAA51F51");

    public static Guid ActiveScheme()
    {
        NativeMethods.Check(NativeMethods.PowerGetActiveScheme(IntPtr.Zero, out var pointer), "PowerGetActiveScheme");
        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            NativeMethods.LocalFree(pointer);
        }
    }

    public static uint Read(Guid subGroup, Guid setting, bool ac)
    {
        var scheme = ActiveScheme();
        uint value;
        var error = ac
            ? NativeMethods.PowerReadACValueIndex(IntPtr.Zero, scheme, subGroup, setting, out value)
            : NativeMethods.PowerReadDCValueIndex(IntPtr.Zero, scheme, subGroup, setting, out value);
        NativeMethods.Check(error, ac ? "PowerReadACValueIndex" : "PowerReadDCValueIndex");
        return value;
    }

    public static void Write(Guid subGroup, Guid setting, bool ac, uint value)
    {
        var scheme = ActiveScheme();
        var error = ac
            ? NativeMethods.PowerWriteACValueIndex(IntPtr.Zero, scheme, subGroup, setting, value)
            : NativeMethods.PowerWriteDCValueIndex(IntPtr.Zero, scheme, subGroup, setting, value);
        NativeMethods.Check(error, ac ? "PowerWriteACValueIndex" : "PowerWriteDCValueIndex");

        // Re-activating the scheme makes the new value effective immediately.
        NativeMethods.Check(NativeMethods.PowerSetActiveScheme(IntPtr.Zero, scheme), "PowerSetActiveScheme");
    }
}
