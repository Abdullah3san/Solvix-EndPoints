namespace Solvix.Modules.Mdm.Providers.Apple;

/// <summary>Builds Apple MDM command property lists (https://developer.apple.com/documentation/devicemanagement/commands_and_queries).</summary>
public static class AppleCommandBuilder
{
    /// <summary>Queries requested on every inventory refresh. Unsupported keys (for example on User Enrollment) are simply omitted by the device.</summary>
    public static readonly string[] InventoryQueries =
    [
        "OSVersion", "BuildVersion", "ModelName", "Model", "ProductName", "SerialNumber", "DeviceName",
        "IsSupervised", "PasscodePresent", "PasscodeCompliant", "HardwareEncryptionCaps",
    ];

    public static byte[] DeviceInformation(Guid commandId) =>
        Wrap(commandId, "DeviceInformation", new Dictionary<string, object?> { ["Queries"] = InventoryQueries.Cast<object?>().ToList() });

    public static byte[] InstalledApplicationList(Guid commandId) =>
        Wrap(commandId, "InstalledApplicationList", new Dictionary<string, object?> { ["ManagedAppsOnly"] = false });

    public static byte[] ManagedApplicationList(Guid commandId) =>
        Wrap(commandId, "ManagedApplicationList", []);

    public static byte[] DeviceLock(Guid commandId, string? message) =>
        Wrap(commandId, "DeviceLock", string.IsNullOrWhiteSpace(message) ? [] : new Dictionary<string, object?> { ["Message"] = message });

    public static byte[] EraseDevice(Guid commandId) =>
        Wrap(commandId, "EraseDevice", new Dictionary<string, object?> { ["PreserveDataPlan"] = true });

    public static byte[] ClearPasscode(Guid commandId, byte[] unlockToken) =>
        Wrap(commandId, "ClearPasscode", new Dictionary<string, object?> { ["UnlockToken"] = unlockToken });

    public static byte[] RestartDevice(Guid commandId) =>
        Wrap(commandId, "RestartDevice", new Dictionary<string, object?> { ["NotifyUser"] = false });

    public static byte[] InstallProfile(Guid commandId, byte[] profile) =>
        Wrap(commandId, "InstallProfile", new Dictionary<string, object?> { ["Payload"] = profile });

    public static byte[] RemoveProfile(Guid commandId, string identifier) =>
        Wrap(commandId, "RemoveProfile", new Dictionary<string, object?> { ["Identifier"] = identifier });

    public static byte[] InstallApplication(Guid commandId, long storeId, IReadOnlyDictionary<string, object?>? configuration, bool removeWithProfile)
    {
        var args = new Dictionary<string, object?>
        {
            ["iTunesStoreID"] = storeId,
            // Managed app: removed when management is removed; backup of its data to iCloud is prevented.
            ["ManagementFlags"] = removeWithProfile ? 5L : 4L,
            ["ChangeManagementState"] = "Managed",
        };
        if (configuration is { Count: > 0 })
        {
            args["Configuration"] = configuration;
        }

        return Wrap(commandId, "InstallApplication", args);
    }

    public static byte[] RemoveApplication(Guid commandId, string bundleIdentifier) =>
        Wrap(commandId, "RemoveApplication", new Dictionary<string, object?> { ["Identifier"] = bundleIdentifier });

    private static byte[] Wrap(Guid commandId, string requestType, Dictionary<string, object?> arguments)
    {
        var command = new Dictionary<string, object?>(arguments) { ["RequestType"] = requestType };
        return ApplePlist.Serialize(new Dictionary<string, object?>
        {
            ["CommandUUID"] = commandId.ToString().ToUpperInvariant(),
            ["Command"] = command,
        });
    }
}
