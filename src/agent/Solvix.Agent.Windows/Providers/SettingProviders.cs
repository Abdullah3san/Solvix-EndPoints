namespace Solvix.Agent.Windows.Providers;

using System.Globalization;
using System.ServiceProcess;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Windows.Interop;
using Solvix.Endpoint.Contracts;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>Windows services through the Service Control Manager.</summary>
internal sealed class ServiceSettingProvider : ISettingProvider
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(60);

    public SettingKind Kind => SettingKind.Service;

    public bool SupportsRollback => true;

    private sealed record ServiceState(ServiceStartupType StartupType, ServiceRunState RunState);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var s = (ServiceSetting)desired;
        var actual = TryRead(s.ServiceName);
        if (actual is null)
        {
            return new Observation(false, false, s.Describe(), "(service not installed)");
        }

        var compliant = (s.StartupType is null || s.StartupType == actual.StartupType)
            && (s.State is null || s.State == actual.RunState);
        return new Observation(true, compliant, Text(s.StartupType, s.State), Text(actual.StartupType, actual.RunState));
    }

    public void Apply(ConfigurationSetting desired)
    {
        var s = (ServiceSetting)desired;
        if (s.StartupType is { } startup)
        {
            SetStartupType(s.ServiceName, startup);
        }

        if (s.State is { } state)
        {
            SetRunState(s.ServiceName, state);
        }
    }

    public string Capture(ConfigurationSetting desired)
    {
        var s = (ServiceSetting)desired;
        return ContractJson.Serialize(TryRead(s.ServiceName) ?? throw new InvalidOperationException($"Service '{s.ServiceName}' is not installed."));
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var s = (ServiceSetting)desired;
        var state = ContractJson.Deserialize<ServiceState>(capturedState) ?? throw new InvalidOperationException("Empty snapshot.");
        SetStartupType(s.ServiceName, state.StartupType);
        SetRunState(s.ServiceName, state.RunState);
    }

    private static ServiceState? TryRead(string name)
    {
        try
        {
            using var controller = new ServiceController(name);
            var startMode = controller.StartType;
            var delayed = startMode == ServiceStartMode.Automatic
                && RegistryHelper.Read($@"SYSTEM\CurrentControlSet\Services\{name}", "DelayedAutostart").Value == "1";
            var startup = startMode switch
            {
                ServiceStartMode.Automatic => delayed ? ServiceStartupType.AutomaticDelayed : ServiceStartupType.Automatic,
                ServiceStartMode.Disabled => ServiceStartupType.Disabled,
                _ => ServiceStartupType.Manual,
            };
            var running = controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
            return new ServiceState(startup, running ? ServiceRunState.Running : ServiceRunState.Stopped);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void SetStartupType(string name, ServiceStartupType startup)
    {
        var manager = NativeMethods.OpenSCManager(null, null, NativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            NativeMethods.ThrowLastError("OpenSCManager");
        }

        try
        {
            var service = NativeMethods.OpenService(manager, name, NativeMethods.ServiceChangeConfig | NativeMethods.ServiceQueryConfig);
            if (service == IntPtr.Zero)
            {
                NativeMethods.ThrowLastError($"OpenService({name})");
            }

            try
            {
                var startType = startup switch
                {
                    ServiceStartupType.Automatic or ServiceStartupType.AutomaticDelayed => NativeMethods.ServiceAutoStart,
                    ServiceStartupType.Disabled => NativeMethods.ServiceDisabled,
                    _ => NativeMethods.ServiceDemandStart,
                };
                if (!NativeMethods.ChangeServiceConfig(service, NativeMethods.ServiceNoChange, startType, NativeMethods.ServiceNoChange, null, null, IntPtr.Zero, null, null, null, null))
                {
                    NativeMethods.ThrowLastError($"ChangeServiceConfig({name})");
                }

                if (startType == NativeMethods.ServiceAutoStart)
                {
                    var info = new NativeMethods.ServiceDelayedAutoStartInfo { DelayedAutostart = startup == ServiceStartupType.AutomaticDelayed ? 1 : 0 };
                    if (!NativeMethods.ChangeServiceConfig2(service, NativeMethods.ServiceConfigDelayedAutoStartInfo, ref info))
                    {
                        NativeMethods.ThrowLastError($"ChangeServiceConfig2({name})");
                    }
                }
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    private static void SetRunState(string name, ServiceRunState state)
    {
        using var controller = new ServiceController(name);
        if (state == ServiceRunState.Running && controller.Status is not (ServiceControllerStatus.Running or ServiceControllerStatus.StartPending))
        {
            controller.Start();
            controller.WaitForStatus(ServiceControllerStatus.Running, StateTimeout);
        }
        else if (state == ServiceRunState.Stopped && controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
        {
            controller.Stop();
            controller.WaitForStatus(ServiceControllerStatus.Stopped, StateTimeout);
        }
    }

    private static string Text(ServiceStartupType? startup, ServiceRunState? state) =>
        string.Join(", ", new[] { startup?.ToString(), state?.ToString() }.Where(x => x is not null));
}

/// <summary>Arbitrary HKLM registry values.</summary>
internal sealed class RegistrySettingProvider : ISettingProvider
{
    public SettingKind Kind => SettingKind.Registry;

    public bool SupportsRollback => true;

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var r = (RegistrySetting)desired;
        var path = RegistrySetting.NormalizePath(r.Path);
        var actual = RegistryHelper.Read(path, r.ValueName);
        if (r.Ensure == PresenceEnsure.Absent)
        {
            return new Observation(true, !actual.ValueExists, "(not set)", RegistryHelper.Describe(actual));
        }

        RegistrySetting.TryNormalizeValue(r.ValueType, r.Value!, out var expected);
        var compliant = actual.ValueExists && actual.Type == r.ValueType && string.Equals(actual.Value, expected, StringComparison.Ordinal);
        return new Observation(true, compliant, $"{expected} ({r.ValueType})", RegistryHelper.Describe(actual));
    }

    public void Apply(ConfigurationSetting desired)
    {
        var r = (RegistrySetting)desired;
        var path = RegistrySetting.NormalizePath(r.Path);
        if (r.Ensure == PresenceEnsure.Absent)
        {
            RegistryHelper.Delete(path, r.ValueName);
        }
        else
        {
            RegistryHelper.Write(path, r.ValueName, r.ValueType, r.Value!);
        }
    }

    public string Capture(ConfigurationSetting desired)
    {
        var r = (RegistrySetting)desired;
        return ContractJson.Serialize(RegistryHelper.Read(RegistrySetting.NormalizePath(r.Path), r.ValueName));
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var r = (RegistrySetting)desired;
        RegistryHelper.Restore(RegistrySetting.NormalizePath(r.Path), r.ValueName, ContractJson.Deserialize<RegistryHelper.ValueState>(capturedState)!);
    }
}

/// <summary>Machine Run/RunOnce entries, disabled the way Task Manager does it (StartupApproved).</summary>
internal sealed class StartupAppSettingProvider : ISettingProvider
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    public SettingKind Kind => SettingKind.StartupApp;

    public bool SupportsRollback => true;

    private sealed record StartupState(RegistryHelper.ValueState Command, RegistryHelper.ValueState Approved);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var s = (StartupAppSetting)desired;
        var command = RegistryHelper.Read(KeyFor(s.Location), s.Name);
        var disabled = IsDisabled(RegistryHelper.Read(ApprovedKey, s.Name));
        var actual = !command.ValueExists ? "(absent)" : disabled ? $"disabled: {command.Value}" : $"enabled: {command.Value}";
        return s.Ensure switch
        {
            StartupEnsure.Absent => new Observation(true, !command.ValueExists, "(absent)", actual),
            StartupEnsure.Disabled => new Observation(command.ValueExists, command.ValueExists && disabled, "disabled", actual),
            _ => new Observation(true, command.ValueExists && !disabled && string.Equals(command.Value, s.Command, StringComparison.Ordinal), $"enabled: {s.Command}", actual),
        };
    }

    public void Apply(ConfigurationSetting desired)
    {
        var s = (StartupAppSetting)desired;
        switch (s.Ensure)
        {
            case StartupEnsure.Present:
                RegistryHelper.Write(KeyFor(s.Location), s.Name, RegistryValueType.String, s.Command!);
                if (s.Location == StartupLocation.MachineRun)
                {
                    RegistryHelper.Write(ApprovedKey, s.Name, RegistryValueType.Binary, "020000000000000000000000");
                }

                break;
            case StartupEnsure.Disabled:
                var fileTime = BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc());
                RegistryHelper.Write(ApprovedKey, s.Name, RegistryValueType.Binary, "03000000" + Convert.ToHexString(fileTime));
                break;
            default:
                RegistryHelper.Delete(KeyFor(s.Location), s.Name);
                RegistryHelper.Delete(ApprovedKey, s.Name);
                break;
        }
    }

    public string Capture(ConfigurationSetting desired)
    {
        var s = (StartupAppSetting)desired;
        return ContractJson.Serialize(new StartupState(RegistryHelper.Read(KeyFor(s.Location), s.Name), RegistryHelper.Read(ApprovedKey, s.Name)));
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var s = (StartupAppSetting)desired;
        var state = ContractJson.Deserialize<StartupState>(capturedState)!;
        RegistryHelper.Restore(KeyFor(s.Location), s.Name, state.Command);
        RegistryHelper.Restore(ApprovedKey, s.Name, state.Approved);
    }

    private static string KeyFor(StartupLocation location) => location == StartupLocation.MachineRunOnce ? RunOnceKey : RunKey;

    private static bool IsDisabled(RegistryHelper.ValueState approved) =>
        approved.ValueExists && approved.Value is { Length: >= 2 } hex && hex[1] is '3';
}

/// <summary>Active power scheme values through the Power Management API.</summary>
internal sealed class PowerSettingProvider : ISettingProvider
{
    public SettingKind Kind => SettingKind.Power;

    public bool SupportsRollback => true;

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var p = (PowerSetting)desired;
        var (sub, setting) = Map(p.Setting);
        var actual = PowerApi.Read(sub, setting, p.Source == PowerSource.Ac);
        return new Observation(true, actual == (uint)p.Value, p.Value.ToString(CultureInfo.InvariantCulture), actual.ToString(CultureInfo.InvariantCulture));
    }

    public void Apply(ConfigurationSetting desired)
    {
        var p = (PowerSetting)desired;
        var (sub, setting) = Map(p.Setting);
        PowerApi.Write(sub, setting, p.Source == PowerSource.Ac, (uint)p.Value);
    }

    public string Capture(ConfigurationSetting desired)
    {
        var p = (PowerSetting)desired;
        var (sub, setting) = Map(p.Setting);
        return PowerApi.Read(sub, setting, p.Source == PowerSource.Ac).ToString(CultureInfo.InvariantCulture);
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var p = (PowerSetting)desired;
        var (sub, setting) = Map(p.Setting);
        PowerApi.Write(sub, setting, p.Source == PowerSource.Ac, uint.Parse(capturedState, CultureInfo.InvariantCulture));
    }

    private static (Guid SubGroup, Guid Setting) Map(PowerSettingType type) => type switch
    {
        PowerSettingType.SleepTimeout => (PowerApi.SubSleep, PowerApi.StandbyIdle),
        PowerSettingType.HibernateTimeout => (PowerApi.SubSleep, PowerApi.HibernateIdle),
        PowerSettingType.DisplayTimeout => (PowerApi.SubVideo, PowerApi.VideoIdle),
        PowerSettingType.LidCloseAction => (PowerApi.SubButtons, PowerApi.LidAction),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}

/// <summary>Machine inactivity limit, sign-in on wake and the secure attention sequence.</summary>
internal sealed class ScreenLockSettingProvider : ISettingProvider
{
    private const string SystemPolicies = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public SettingKind Kind => SettingKind.ScreenLock;

    public bool SupportsRollback => true;

    private sealed record LockState(RegistryHelper.ValueState? Registry, uint? Ac, uint? Dc);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var l = (ScreenLockSetting)desired;
        var expected = l.Value.ToString(CultureInfo.InvariantCulture);
        switch (l.Control)
        {
            case ScreenLockControl.RequirePasswordOnWake:
                var ac = PowerApi.Read(PowerApi.SubNone, PowerApi.ConsoleLock, true);
                var dc = PowerApi.Read(PowerApi.SubNone, PowerApi.ConsoleLock, false);
                return new Observation(true, ac == (uint)l.Value && dc == (uint)l.Value, $"AC={l.Value}, DC={l.Value}", $"AC={ac}, DC={dc}");
            case ScreenLockControl.RequireCtrlAltDel:
                // DisableCAD=0 means CTRL+ALT+DEL is required.
                var cad = RegistryHelper.Read(SystemPolicies, "DisableCAD");
                var required = cad.ValueExists && cad.Value == "0";
                return new Observation(true, required == (l.Value == 1), l.Value == 1 ? "required" : "not required", required ? "required" : "not required");
            default:
                var timeout = RegistryHelper.Read(SystemPolicies, "InactivityTimeoutSecs");
                return new Observation(true, timeout.ValueExists && timeout.Type == RegistryValueType.DWord && timeout.Value == expected, $"{expected}s", RegistryHelper.Describe(timeout));
        }
    }

    public void Apply(ConfigurationSetting desired)
    {
        var l = (ScreenLockSetting)desired;
        switch (l.Control)
        {
            case ScreenLockControl.RequirePasswordOnWake:
                PowerApi.Write(PowerApi.SubNone, PowerApi.ConsoleLock, true, (uint)l.Value);
                PowerApi.Write(PowerApi.SubNone, PowerApi.ConsoleLock, false, (uint)l.Value);
                break;
            case ScreenLockControl.RequireCtrlAltDel:
                RegistryHelper.Write(SystemPolicies, "DisableCAD", RegistryValueType.DWord, l.Value == 1 ? "0" : "1");
                break;
            default:
                RegistryHelper.Write(SystemPolicies, "InactivityTimeoutSecs", RegistryValueType.DWord, l.Value.ToString(CultureInfo.InvariantCulture));
                break;
        }
    }

    public string Capture(ConfigurationSetting desired)
    {
        var l = (ScreenLockSetting)desired;
        return ContractJson.Serialize(l.Control switch
        {
            ScreenLockControl.RequirePasswordOnWake => new LockState(null, PowerApi.Read(PowerApi.SubNone, PowerApi.ConsoleLock, true), PowerApi.Read(PowerApi.SubNone, PowerApi.ConsoleLock, false)),
            ScreenLockControl.RequireCtrlAltDel => new LockState(RegistryHelper.Read(SystemPolicies, "DisableCAD"), null, null),
            _ => new LockState(RegistryHelper.Read(SystemPolicies, "InactivityTimeoutSecs"), null, null),
        });
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        var l = (ScreenLockSetting)desired;
        var state = ContractJson.Deserialize<LockState>(capturedState)!;
        switch (l.Control)
        {
            case ScreenLockControl.RequirePasswordOnWake:
                PowerApi.Write(PowerApi.SubNone, PowerApi.ConsoleLock, true, state.Ac!.Value);
                PowerApi.Write(PowerApi.SubNone, PowerApi.ConsoleLock, false, state.Dc!.Value);
                break;
            case ScreenLockControl.RequireCtrlAltDel:
                RegistryHelper.Restore(SystemPolicies, "DisableCAD", state.Registry!);
                break;
            default:
                RegistryHelper.Restore(SystemPolicies, "InactivityTimeoutSecs", state.Registry!);
                break;
        }
    }
}

/// <summary>"Removable Storage Access" administrative template policies.</summary>
internal sealed class UsbStorageSettingProvider : ISettingProvider
{
    private const string Root = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";

    public SettingKind Kind => SettingKind.UsbStorage;

    public bool SupportsRollback => true;

    private sealed record UsbValue(string Path, string Name);

    public Observation Evaluate(ConfigurationSetting desired)
    {
        var u = (UsbStorageSetting)desired;
        var (readValues, writeValues) = Values(u.DeviceClass);
        var denyRead = readValues.All(v => IsSet(v));
        var denyWrite = writeValues.All(v => IsSet(v));
        var anyRead = readValues.Any(v => IsSet(v));
        var anyWrite = writeValues.Any(v => IsSet(v));
        var actual = denyRead ? UsbAccess.NoAccess : denyWrite && !anyRead ? UsbAccess.ReadOnly : !anyRead && !anyWrite ? UsbAccess.FullAccess : (UsbAccess?)null;
        return new Observation(true, actual == u.Access, u.Access.ToString(), actual?.ToString() ?? "partially restricted");
    }

    public void Apply(ConfigurationSetting desired)
    {
        var u = (UsbStorageSetting)desired;
        var (readValues, writeValues) = Values(u.DeviceClass);
        foreach (var v in readValues)
        {
            Set(v, u.Access == UsbAccess.NoAccess);
        }

        foreach (var v in writeValues.Except(readValues))
        {
            Set(v, u.Access is UsbAccess.NoAccess or UsbAccess.ReadOnly);
        }
    }

    public string Capture(ConfigurationSetting desired)
    {
        var u = (UsbStorageSetting)desired;
        var (readValues, writeValues) = Values(u.DeviceClass);
        return ContractJson.Serialize(readValues.Union(writeValues).Select(v => new KeyValuePair<string, RegistryHelper.ValueState>($"{v.Path}|{v.Name}", RegistryHelper.Read(v.Path, v.Name))).ToList());
    }

    public void Restore(ConfigurationSetting desired, string capturedState)
    {
        foreach (var (id, state) in ContractJson.Deserialize<List<KeyValuePair<string, RegistryHelper.ValueState>>>(capturedState)!)
        {
            var parts = id.Split('|');
            RegistryHelper.Restore(parts[0], parts[1], state);
        }
    }

    private static (UsbValue[] Read, UsbValue[] Write) Values(UsbDeviceClass deviceClass)
    {
        static UsbValue[] For(string name, params string[] guids) => guids.Select(g => new UsbValue($@"{Root}\{g}", name)).ToArray();
        return deviceClass switch
        {
            UsbDeviceClass.RemovableDisks => (For("Deny_Read", "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}"), For("Deny_Write", "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}")),
            UsbDeviceClass.CdDvd => (For("Deny_Read", "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}"), For("Deny_Write", "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}")),
            UsbDeviceClass.PortableDevices => (
                For("Deny_Read", "{6AC27878-A6FA-4155-BA85-F98F491D4F33}", "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}"),
                For("Deny_Write", "{6AC27878-A6FA-4155-BA85-F98F491D4F33}", "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}")),
            _ => ([new UsbValue(Root, "Deny_All")], [new UsbValue(Root, "Deny_All")]),
        };
    }

    private static bool IsSet(UsbValue v) => RegistryHelper.Read(v.Path, v.Name) is { ValueExists: true, Value: "1" };

    private static void Set(UsbValue v, bool deny)
    {
        if (deny)
        {
            RegistryHelper.Write(v.Path, v.Name, RegistryValueType.DWord, "1");
        }
        else
        {
            RegistryHelper.Delete(v.Path, v.Name);
        }
    }
}
