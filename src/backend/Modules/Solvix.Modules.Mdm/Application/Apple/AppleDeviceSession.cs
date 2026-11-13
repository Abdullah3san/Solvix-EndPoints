namespace Solvix.Modules.Mdm.Application.Apple;

using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Application.Commands;
using Solvix.Modules.Mdm.Application.Compliance;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Apple;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

/// <summary>HTTP-level outcome of an MDM protocol exchange. A null body means an empty 200.</summary>
internal sealed record AppleProtocolResponse(int StatusCode, byte[]? Body)
{
    public static AppleProtocolResponse Ok(byte[]? body = null) => new(200, body);
}

/// <summary>
/// The tenant-bound half of the Apple MDM protocol: state changes for one device. Always created from a
/// tenant scope, so row-level security and the tenant query filters apply exactly as for an administrator request.
/// </summary>
internal sealed class AppleDeviceSession(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    IClock clock,
    IOptions<MdmOptions> options,
    ISecretProtector protector,
    IComplianceService compliance,
    IApplePushClient push,
    ILogger<AppleDeviceSession> logger)
{
    public async Task<(byte[] Profile, Guid DeviceId)> CreateEnrollmentAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var apple = options.Value.Apple;
        var token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == tokenId, cancellationToken)
            ?? throw new NotFoundException("Enrollment", tokenId);
        var now = clock.UtcNow;
        token.RegisterUse(now);

        var handle = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var device = MobileDevice.CreatePending(tenantContext.RequireTenantId(), token.Scenario, handle, token.Id, token.PolicyId);
        db.Set<MobileDevice>().Add(device);
        await db.SaveChangesAsync(cancellationToken);

        var profile = AppleProfileBuilder.BuildEnrollmentProfile(new AppleEnrollmentProfileOptions(
            handle, token.Scenario, options.Value.PublicBaseUrl, apple.PushTopic!, apple.ScepUrl!, apple.ScepChallenge, apple.OrganizationName));
        return (profile, device.Id);
    }

    public async Task<AppleProtocolResponse> CheckInAsync(Guid deviceId, Dictionary<string, object?> message, string thumbprint, CancellationToken cancellationToken)
    {
        var device = await LoadAsync(deviceId, cancellationToken);
        var type = Str(message, "MessageType");
        var now = clock.UtcNow;

        if (device.State is DeviceState.Retired or DeviceState.Wiped)
        {
            throw new ForbiddenException("This device has been retired and must enroll again.");
        }

        if (type == "Authenticate")
        {
            RequireTopic(message);
            device.BindIdentity(thumbprint);
            device.CompleteEnrollmentIdentifier(Str(message, "UDID") ?? Str(message, "EnrollmentID"));
            device.ApplyInventory(new DeviceInventory
            {
                DisplayName = Str(message, "DeviceName"),
                Manufacturer = "Apple",
                Model = Str(message, "ModelName") ?? Str(message, "Model") ?? Str(message, "ProductName"),
                SerialNumber = Str(message, "SerialNumber"),
                OsVersion = Str(message, "OSVersion"),
            }, now);
            await db.SaveChangesAsync(cancellationToken);
            return AppleProtocolResponse.Ok();
        }

        if (device.IdentityThumbprint is null)
        {
            throw new ForbiddenException("The device has not authenticated yet.");
        }

        device.BindIdentity(thumbprint);
        switch (type)
        {
            case "TokenUpdate":
                // The user channel (shared iPad) has its own TokenUpdate; only the device channel drives this device.
                if (message.ContainsKey("UserID"))
                {
                    return AppleProtocolResponse.Ok();
                }

                RequireTopic(message);
                if (await HandleTokenUpdateAsync(device, message, now, cancellationToken))
                {
                    await db.SaveChangesAsync(cancellationToken);
                    await WakeAsync(device, cancellationToken);
                    return AppleProtocolResponse.Ok();
                }

                break;
            case "CheckOut":
                await HandleCheckOutAsync(device, now, cancellationToken);
                break;
            case "UserAuthenticate":
                // User-channel authentication is not offered: Apple's documented answer is 410.
                return new AppleProtocolResponse(410, null);
            default:
                logger.LogDebug("Ignoring Apple check-in message {Type}", type);
                break;
        }

        await db.SaveChangesAsync(cancellationToken);
        return AppleProtocolResponse.Ok();
    }

    public async Task<AppleProtocolResponse> ServerAsync(Guid deviceId, Dictionary<string, object?> message, string thumbprint, CancellationToken cancellationToken)
    {
        var device = await LoadAsync(deviceId, cancellationToken);
        if (device.IdentityThumbprint is null)
        {
            throw new ForbiddenException("The device has not authenticated yet.");
        }

        device.BindIdentity(thumbprint);
        if (!device.IsActive)
        {
            // Apple: HTTP 410 tells the device to stop contacting the server.
            return new AppleProtocolResponse(410, null);
        }

        var now = clock.UtcNow;
        device.RecordContact(now);
        Guid? handled = null;
        var status = Str(message, "Status");
        if (Guid.TryParse(Str(message, "CommandUUID"), out var commandId))
        {
            var command = await db.Set<DeviceCommand>().FirstOrDefaultAsync(c => c.Id == commandId && c.DeviceId == device.Id, cancellationToken);
            if (command is not null)
            {
                handled = command.Id;
                await HandleStatusAsync(device, command, status, message, now, cancellationToken);
            }
        }

        // Persist what the response produced (follow-up commands, inventory) so the next-command query sees it.
        await db.SaveChangesAsync(cancellationToken);
        if (!device.IsActive)
        {
            return AppleProtocolResponse.Ok();
        }

        // Hand out the next queued command (skipping one the device just deferred).
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var next = await db.Set<DeviceCommand>()
                .Where(c => c.DeviceId == device.Id && c.State == CommandState.Queued && c.Id != handled)
                .OrderBy(c => c.CreatedAtUtc).ThenBy(c => c.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is null)
            {
                break;
            }

            if (next.ExpireIfDue(now))
            {
                continue;
            }

            try
            {
                var payload = await BuildPayloadAsync(device, next, cancellationToken);
                next.MarkSent(next.Id.ToString("N"), now);
                await db.SaveChangesAsync(cancellationToken);
                return AppleProtocolResponse.Ok(payload);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotFoundException or ConflictException or CryptographicException)
            {
                logger.LogWarning(ex, "Command {CommandId} could not be built for device {DeviceId}", next.Id, device.Id);
                next.MarkFailed(ex.Message, now);
                device.CancelRemoval();
                DeviceLifecycle.Record(db, device, DeviceEventType.CommandFailed, $"{next.Type} failed: {ex.Message}", null, next.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return AppleProtocolResponse.Ok();
    }

    /// <summary>A freshly enrolled device does not poll by itself: it must be pushed once so it collects the first commands.</summary>
    private async Task WakeAsync(MobileDevice device, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(device.PushToken) || string.IsNullOrEmpty(device.PushMagic))
        {
            return;
        }

        try
        {
            await push.SendMdmPushAsync(device.PushToken, device.PushMagic, device.PushTopic ?? options.Value.Apple.PushTopic!, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "APNs push failed for newly enrolled device {DeviceId}; the sync worker will retry", device.Id);
        }
    }

    /// <returns>True when this TokenUpdate completed the enrollment (first contact).</returns>
    private async Task<bool> HandleTokenUpdateAsync(MobileDevice device, Dictionary<string, object?> message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var token = message.GetValueOrDefault("Token") as byte[] ?? throw new ValidationException("Token", "TokenUpdate is missing the push token.");
        var magic = Str(message, "PushMagic") ?? throw new ValidationException("PushMagic", "TokenUpdate is missing PushMagic.");
        var unlock = message.GetValueOrDefault("UnlockToken") as byte[];
        device.BindAppleTransport(
            Convert.ToHexString(token).ToLowerInvariant(),
            magic,
            Str(message, "Topic"),
            unlock is null ? null : protector.Protect(Convert.ToBase64String(unlock)));

        if (device.State != DeviceState.Pending)
        {
            return false;
        }

        device.CompleteEnrollment(now);
        DeviceLifecycle.Record(db, device, DeviceEventType.Enrolled,
            $"Enrolled as {device.Mode} ({device.Ownership}).");

        // First contact: deliver the policy and learn what the device is.
        if (device.PolicyId is { } policyId)
        {
            var policy = await db.Set<MobilePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
            Queue(device, DeviceCommandType.ApplyPolicy, "Initial policy.", new CommandParameters(PolicyVersion: policy?.PublishedVersion).ToJson(), now);
        }

        Queue(device, DeviceCommandType.SyncInventory, "Initial inventory.", null, now);
        return true;
    }

    private async Task HandleCheckOutAsync(MobileDevice device, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var retire = await db.Set<DeviceCommand>()
            .Where(c => c.DeviceId == device.Id && c.Type == DeviceCommandType.Retire && (c.State == CommandState.Sent || c.State == CommandState.Queued))
            .FirstOrDefaultAsync(cancellationToken);
        retire?.MarkAcknowledged(now);
        DeviceLifecycle.CompleteRemoval(db, device, DeviceCommandType.Retire, now, retire?.DecidedByUserId, retire?.Id);
    }

    private async Task HandleStatusAsync(
        MobileDevice device,
        DeviceCommand command,
        string? status,
        Dictionary<string, object?> message,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        switch (status)
        {
            case "Acknowledged":
                command.MarkAcknowledged(now);
                await ApplyResponseAsync(device, command, message, now, cancellationToken);
                break;
            case "NotNow":
                command.Requeue();
                break;
            case "Error" or "CommandFormatError":
                var error = SummarizeError(message);
                command.MarkFailed(error, now);
                device.CancelRemoval();
                DeviceLifecycle.Record(db, device, DeviceEventType.CommandFailed, $"{command.Type} failed on the device: {error}", null, command.Id);
                break;
            default:
                break; // Idle or unknown: nothing to record.
        }
    }

    private async Task ApplyResponseAsync(MobileDevice device, DeviceCommand command, Dictionary<string, object?> message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var parameters = CommandParameters.Parse(command.ParametersJson);
        switch (command.Type)
        {
            case DeviceCommandType.SyncInventory when parameters.Stage == "apps":
                var apps = (message.GetValueOrDefault("InstalledApplicationList") as List<object?> ?? [])
                    .OfType<Dictionary<string, object?>>()
                    .Where(a => Str(a, "Identifier") is not null)
                    .Select(a => new InstalledApp(Str(a, "Identifier")!, Str(a, "Name"), Str(a, "ShortVersion") ?? Str(a, "Version"), IsManaged: device.Ownership == DeviceOwnership.PersonallyOwned))
                    .ToList();
                device.ApplyInventory(new DeviceInventory { Apps = apps }, now);
                DeviceLifecycle.Record(db, device, DeviceEventType.InventoryUpdated, $"Application inventory updated ({apps.Count} apps).", null, command.Id);
                await compliance.EvaluateAsync(device, cancellationToken);
                break;

            case DeviceCommandType.SyncInventory:
                var info = message.GetValueOrDefault("QueryResponses") as Dictionary<string, object?> ?? [];
                device.ApplyInventory(new DeviceInventory
                {
                    DisplayName = Str(info, "DeviceName"),
                    Manufacturer = "Apple",
                    Model = Str(info, "ModelName") ?? Str(info, "Model") ?? Str(info, "ProductName"),
                    SerialNumber = Str(info, "SerialNumber"),
                    OsVersion = Str(info, "OSVersion"),
                    IsSupervised = info.GetValueOrDefault("IsSupervised") as bool?,
                    HasPasscode = info.GetValueOrDefault("PasscodePresent") as bool?,
                    IsEncrypted = info.GetValueOrDefault("HardwareEncryptionCaps") is long caps ? caps > 0 : null,
                }, now);
                DeviceLifecycle.Record(db, device, DeviceEventType.InventoryUpdated, "Device inventory updated.", null, command.Id);
                Queue(device, DeviceCommandType.SyncInventory, "Application inventory.", new CommandParameters(Stage: "apps").ToJson(), now);
                await compliance.EvaluateAsync(device, cancellationToken);
                break;

            case DeviceCommandType.ApplyPolicy:
                if (device.PolicyId is { } policyId)
                {
                    var policy = await db.Set<MobilePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
                    if (policy is not null)
                    {
                        device.MarkPolicyApplied(parameters.PolicyVersion ?? policy.PublishedVersion);
                        await QueueRequiredAppsAsync(device, policy, now, cancellationToken);
                    }
                }

                DeviceLifecycle.Record(db, device, DeviceEventType.PolicyApplied, "Device confirmed the policy profile.", null, command.Id);
                await compliance.EvaluateAsync(device, cancellationToken);
                break;

            case DeviceCommandType.Wipe or DeviceCommandType.Retire:
                DeviceLifecycle.CompleteRemoval(db, device, command.Type, now, command.DecidedByUserId, command.Id);
                break;

            default:
                DeviceLifecycle.Record(db, device, DeviceEventType.CommandCompleted, $"{command.Type} completed.", command.RequestedByUserId, command.Id);
                break;
        }
    }

    private async Task QueueRequiredAppsAsync(MobileDevice device, MobilePolicy policy, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!MdmCapabilities.For(device.Scenario).Supports(DeviceCommandType.InstallApp))
        {
            return;
        }

        var installed = device.InstalledApps.Select(a => a.Identifier).ToHashSet(StringComparer.Ordinal);
        var assignments = await db.Set<MobilePolicyApp>().AsNoTracking()
            .Where(a => a.PolicyId == policy.Id && a.InstallType == AppInstallType.ForceInstalled).ToListAsync(cancellationToken);
        var ids = assignments.Select(a => a.AppId).ToList();
        var apps = await db.Set<MobileApp>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(cancellationToken);
        foreach (var app in apps.Where(a => a.StoreId is not null && !installed.Contains(a.Identifier)))
        {
            Queue(device, DeviceCommandType.InstallApp, $"Required by policy '{policy.Name}'.", new CommandParameters(AppId: app.Id).ToJson(), now);
        }
    }

    private async Task<byte[]> BuildPayloadAsync(MobileDevice device, DeviceCommand command, CancellationToken cancellationToken)
    {
        var parameters = CommandParameters.Parse(command.ParametersJson);
        switch (command.Type)
        {
            case DeviceCommandType.SyncInventory:
                return parameters.Stage == "apps" ? AppleCommandBuilder.InstalledApplicationList(command.Id) : AppleCommandBuilder.DeviceInformation(command.Id);
            case DeviceCommandType.ApplyPolicy:
                var policyId = device.PolicyId ?? throw new InvalidOperationException("The device has no policy assigned any more.");
                var policy = await db.Set<MobilePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken)
                    ?? throw new NotFoundException("Policy", policyId);
                var specs = await LoadSpecsAsync(policyId, cancellationToken);
                var translation = AppleProfileBuilder.BuildPolicyProfile(policy.Id, policy.Name, policy.Version, policy.Scenario, policy.Settings, specs, protector.Unprotect);
                return AppleCommandBuilder.InstallProfile(command.Id, translation.Profile);
            case DeviceCommandType.Lock:
                return AppleCommandBuilder.DeviceLock(command.Id, parameters.Message);
            case DeviceCommandType.ResetPasscode:
                var unlock = protector.Unprotect(device.ProtectedUnlockToken)
                    ?? throw new InvalidOperationException("The device did not provide an unlock token, so its passcode cannot be cleared remotely.");
                return AppleCommandBuilder.ClearPasscode(command.Id, Convert.FromBase64String(unlock));
            case DeviceCommandType.Reboot:
                return AppleCommandBuilder.RestartDevice(command.Id);
            case DeviceCommandType.Wipe:
                return AppleCommandBuilder.EraseDevice(command.Id);
            case DeviceCommandType.Retire:
                return AppleCommandBuilder.RemoveProfile(command.Id, AppleProfileBuilder.EnrollmentIdentifier(device.ProviderDeviceId));
            case DeviceCommandType.InstallApp:
                var app = await LoadAppAsync(parameters, cancellationToken);
                var storeId = app.StoreId ?? throw new InvalidOperationException("The application has no App Store id.");
                return AppleCommandBuilder.InstallApplication(command.Id, storeId, await ManagedConfigurationAsync(device, app, cancellationToken), removeWithProfile: true);
            case DeviceCommandType.RemoveApp:
                return AppleCommandBuilder.RemoveApplication(command.Id, (await LoadAppAsync(parameters, cancellationToken)).Identifier);
            default:
                throw new InvalidOperationException($"{command.Type} is not an Apple device command.");
        }
    }

    private async Task<IReadOnlyDictionary<string, object?>?> ManagedConfigurationAsync(MobileDevice device, MobileApp app, CancellationToken cancellationToken)
    {
        if (device.PolicyId is not { } policyId)
        {
            return null;
        }

        var json = (await db.Set<MobilePolicyApp>().AsNoTracking().FirstOrDefaultAsync(a => a.PolicyId == policyId && a.AppId == app.Id, cancellationToken))?.ManagedConfigurationJson;
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.EnumerateObject().ToDictionary(
            p => p.Name,
            p => p.Value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => (object?)true,
                System.Text.Json.JsonValueKind.False => false,
                System.Text.Json.JsonValueKind.Number => p.Value.TryGetInt64(out var l) ? l : p.Value.GetDouble(),
                _ => p.Value.GetString() ?? string.Empty,
            });
    }

    private async Task<MobileApp> LoadAppAsync(CommandParameters parameters, CancellationToken cancellationToken)
    {
        var appId = parameters.AppId ?? throw new InvalidOperationException("The command has no application.");
        return await db.Set<MobileApp>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken) ?? throw new NotFoundException("Application", appId);
    }

    private async Task<IReadOnlyList<PolicyAppSpec>> LoadSpecsAsync(Guid policyId, CancellationToken cancellationToken)
    {
        var assignments = await db.Set<MobilePolicyApp>().AsNoTracking().Where(a => a.PolicyId == policyId).ToListAsync(cancellationToken);
        var ids = assignments.Select(a => a.AppId).ToList();
        var catalogue = await db.Set<MobileApp>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, cancellationToken);
        return assignments.Where(a => catalogue.ContainsKey(a.AppId))
            .Select(a => new PolicyAppSpec(catalogue[a.AppId].Identifier, catalogue[a.AppId].Name, a.InstallType, catalogue[a.AppId].StoreId, a.ManagedConfigurationJson))
            .ToList();
    }

    private void Queue(MobileDevice device, DeviceCommandType type, string reason, string? parametersJson, DateTimeOffset now) =>
        db.Set<DeviceCommand>().Add(DeviceCommand.Create(
            device.TenantId, device.Id, type, Guid.Empty, reason, parametersJson, now, TimeSpan.FromHours(options.Value.CommandTimeToLiveHours)));

    private void RequireTopic(Dictionary<string, object?> message)
    {
        var expected = options.Value.Apple.PushTopic;
        if (Str(message, "Topic") is { } topic && !string.Equals(topic, expected, StringComparison.Ordinal))
        {
            throw new ValidationException("Topic", "The message was addressed to a different MDM topic.");
        }
    }

    private async Task<MobileDevice> LoadAsync(Guid deviceId, CancellationToken cancellationToken) =>
        await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == deviceId, cancellationToken) ?? throw new NotFoundException("Device", deviceId);

    private static string? Str(Dictionary<string, object?> dict, string key) => dict.GetValueOrDefault(key) as string;

    private static string SummarizeError(Dictionary<string, object?> message)
    {
        if (message.GetValueOrDefault("ErrorChain") is List<object?> chain && chain.OfType<Dictionary<string, object?>>().FirstOrDefault() is { } first)
        {
            var code = first.GetValueOrDefault("ErrorCode") is long l ? l.ToString(CultureInfo.InvariantCulture) : "?";
            return $"{Str(first, "USEnglishDescription") ?? Str(first, "LocalizedDescription") ?? "Device reported an error"} (code {code})";
        }

        return "The device reported an error.";
    }
}
