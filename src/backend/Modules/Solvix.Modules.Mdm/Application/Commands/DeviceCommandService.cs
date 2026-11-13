namespace Solvix.Modules.Mdm.Application.Commands;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Application.Devices;
using Solvix.Modules.Mdm.Application.Enterprise;
using Solvix.Modules.Mdm.Domain;
using Solvix.Modules.Mdm.Providers;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.Modules.Mdm.Security;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Results;

public interface IDeviceCommandService
{
    Task<CommandDto> CreateAsync(Guid deviceId, CreateCommandRequest request, CancellationToken cancellationToken);

    Task<CommandDto> GetAsync(Guid commandId, CancellationToken cancellationToken);

    Task<PagedResult<CommandDto>> ListAsync(CommandListQuery query, CancellationToken cancellationToken);

    Task<CommandDto> ApproveAsync(Guid commandId, CommandDecisionRequest request, CancellationToken cancellationToken);

    Task<CommandDto> RejectAsync(Guid commandId, CommandDecisionRequest request, CancellationToken cancellationToken);

    Task<CommandDto> CancelAsync(Guid commandId, CancellationToken cancellationToken);
}

/// <summary>Internal entry point for commands the platform itself raises (policy changes, inventory refresh).</summary>
internal interface IDeviceCommandQueue
{
    /// <summary>Queues and dispatches a command that never needs approval. Throws for destructive types.</summary>
    Task<DeviceCommand> EnqueueSystemAsync(MobileDevice device, DeviceCommandType type, string? reason, string? parametersJson, CancellationToken cancellationToken);

    /// <summary>Polls Android long-running operations for commands that were handed to Google.</summary>
    Task<int> ReconcileAndroidOperationsAsync(CancellationToken cancellationToken);

    /// <summary>Pushes Apple devices again when commands have waited too long (lost or throttled push).</summary>
    Task<int> RepushStaleAppleCommandsAsync(CancellationToken cancellationToken);

    /// <summary>Expires commands whose window elapsed and restores devices stuck waiting for them.</summary>
    Task<int> ExpireAsync(CancellationToken cancellationToken);
}

internal sealed record CommandParameters(Guid? AppId = null, string? Message = null, string? Stage = null, int? PolicyVersion = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static CommandParameters Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new CommandParameters() : JsonSerializer.Deserialize<CommandParameters>(json, Json) ?? new CommandParameters();
}

internal sealed class DeviceCommandService(
    ISolvixDbContext db,
    ICurrentUser currentUser,
    IClock clock,
    IOptions<MdmOptions> options,
    IMdmProviderRegistry providers,
    IEnterpriseDirectory enterprises,
    IAndroidManagementApi android,
    ISecretProtector protector,
    ILogger<DeviceCommandService> logger) : IDeviceCommandService, IDeviceCommandQueue
{
    private const int MaxMessageLength = 200;

    public async Task<CommandDto> CreateAsync(Guid deviceId, CreateCommandRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await FindDeviceAsync(deviceId, cancellationToken);
        if (device.State != DeviceState.Enrolled)
        {
            throw new ConflictException($"Commands can only be sent to enrolled devices (current state: {device.State}).");
        }

        MdmCapabilities.EnsureSupported(device.Scenario, request.Type);
        _ = providers.For(device.Platform);
        var parameters = await ValidateParametersAsync(device, request, cancellationToken);

        if (MdmCapabilities.RequiresApproval(request.Type)
            && await db.Set<DeviceCommand>().AnyAsync(
                c => c.DeviceId == deviceId && (c.Type == DeviceCommandType.Retire || c.Type == DeviceCommandType.Wipe)
                     && (c.State == CommandState.PendingApproval || c.State == CommandState.Queued || c.State == CommandState.Sent),
                cancellationToken))
        {
            throw new ConflictException("A retire or wipe is already pending for this device.");
        }

        var command = DeviceCommand.Create(
            device.TenantId, device.Id, request.Type, currentUser.RequireUserId(), request.Reason, parameters.ToJson(), clock.UtcNow, TimeToLive);
        db.Set<DeviceCommand>().Add(command);
        DeviceLifecycle.Record(db, device, DeviceEventType.CommandRequested,
            command.RequiresApproval ? $"{request.Type} requested; awaiting approval." : $"{request.Type} requested.", command.RequestedByUserId, command.Id);

        if (command.State == CommandState.Queued)
        {
            await DispatchAsync(device, command, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return CommandDto.From(command);
    }

    public async Task<CommandDto> GetAsync(Guid commandId, CancellationToken cancellationToken) =>
        CommandDto.From(await FindCommandAsync(commandId, cancellationToken));

    public async Task<PagedResult<CommandDto>> ListAsync(CommandListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = new PageRequest(query.Page, query.PageSize);
        var commands = db.Set<DeviceCommand>().AsNoTracking();
        if (query.DeviceId is { } deviceId)
        {
            commands = commands.Where(c => c.DeviceId == deviceId);
        }

        if (query.State is { } state)
        {
            commands = commands.Where(c => c.State == state);
        }

        var total = await commands.LongCountAsync(cancellationToken);
        var items = await commands.OrderByDescending(c => c.CreatedAtUtc).Skip(page.Skip).Take(page.SafePageSize).ToListAsync(cancellationToken);
        return new PagedResult<CommandDto>(items.Select(CommandDto.From).ToList(), page.SafePage, page.SafePageSize, total);
    }

    public async Task<CommandDto> ApproveAsync(Guid commandId, CommandDecisionRequest request, CancellationToken cancellationToken)
    {
        var command = await FindCommandAsync(commandId, cancellationToken);
        var device = await FindDeviceAsync(command.DeviceId, cancellationToken);
        var approver = currentUser.RequireUserId();
        if (device.State != DeviceState.Enrolled)
        {
            throw new ConflictException($"The device is no longer enrolled (state: {device.State}).");
        }

        // Re-check at approval time: capabilities and provider configuration may have changed since the request.
        MdmCapabilities.EnsureSupported(device.Scenario, command.Type);
        _ = providers.For(device.Platform);

        command.Approve(approver, request?.Note, clock.UtcNow, options.Value.AllowSelfApproval);
        device.BeginRemoval();
        DeviceLifecycle.Record(db, device, DeviceEventType.CommandApproved, $"{command.Type} approved.", approver, command.Id);
        await DispatchAsync(device, command, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return CommandDto.From(command);
    }

    public async Task<CommandDto> RejectAsync(Guid commandId, CommandDecisionRequest request, CancellationToken cancellationToken)
    {
        var command = await FindCommandAsync(commandId, cancellationToken);
        var device = await FindDeviceAsync(command.DeviceId, cancellationToken);
        var approver = currentUser.RequireUserId();
        command.Reject(approver, request?.Note, clock.UtcNow);
        DeviceLifecycle.Record(db, device, DeviceEventType.CommandRejected, $"{command.Type} rejected.", approver, command.Id);
        await db.SaveChangesAsync(cancellationToken);
        return CommandDto.From(command);
    }

    public async Task<CommandDto> CancelAsync(Guid commandId, CancellationToken cancellationToken)
    {
        var command = await FindCommandAsync(commandId, cancellationToken);
        var device = await FindDeviceAsync(command.DeviceId, cancellationToken);
        var wasRemoval = command.RequiresApproval && command.State == CommandState.Queued;
        command.Cancel(clock.UtcNow);
        if (wasRemoval)
        {
            device.CancelRemoval();
        }

        DeviceLifecycle.Record(db, device, DeviceEventType.CommandRejected, $"{command.Type} cancelled.", currentUser.UserId, command.Id);
        await db.SaveChangesAsync(cancellationToken);
        return CommandDto.From(command);
    }

    public async Task<DeviceCommand> EnqueueSystemAsync(MobileDevice device, DeviceCommandType type, string? reason, string? parametersJson, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (MdmCapabilities.RequiresApproval(type))
        {
            throw new InvalidOperationException($"{type} requires approval and cannot be enqueued by the system.");
        }

        MdmCapabilities.EnsureSupported(device.Scenario, type);
        var command = DeviceCommand.Create(device.TenantId, device.Id, type, currentUser.UserId ?? Guid.Empty, reason, parametersJson, clock.UtcNow, TimeToLive);
        db.Set<DeviceCommand>().Add(command);
        await DispatchAsync(device, command, cancellationToken);
        return command;
    }

    public async Task<int> ReconcileAndroidOperationsAsync(CancellationToken cancellationToken)
    {
        var open = await db.Set<DeviceCommand>()
            .Where(c => c.State == CommandState.Sent && c.ProviderReference != null && c.ProviderReference.StartsWith("enterprises/"))
            .ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        var changed = 0;
        foreach (var command in open)
        {
            var device = await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == command.DeviceId, cancellationToken);
            if (device is null)
            {
                continue;
            }

            AndroidOperationInfo operation;
            try
            {
                operation = await android.GetOperationAsync(command.ProviderReference!, cancellationToken);
            }
            catch (AndroidManagementApiException ex)
            {
                logger.LogWarning(ex, "Could not read operation for command {CommandId}", command.Id);
                continue;
            }

            if (!operation.Done)
            {
                continue;
            }

            changed++;
            if (operation.Error is { } error)
            {
                command.MarkFailed(error, now);
                device.CancelRemoval();
                DeviceLifecycle.Record(db, device, DeviceEventType.CommandFailed, $"{command.Type} failed: {error}", null, command.Id);
                continue;
            }

            command.MarkAcknowledged(now);
            DeviceLifecycle.Record(db, device, DeviceEventType.CommandCompleted, $"{command.Type} completed.", null, command.Id);
            if (command.Type is DeviceCommandType.Retire or DeviceCommandType.Wipe)
            {
                DeviceLifecycle.CompleteRemoval(db, device, command.Type, now, command.DecidedByUserId, command.Id);
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    public async Task<int> RepushStaleAppleCommandsAsync(CancellationToken cancellationToken)
    {
        if (!providers.IsConfigured(MobilePlatform.Ios))
        {
            return 0;
        }

        var cutoff = clock.UtcNow.AddMinutes(-5);
        var stale = await db.Set<DeviceCommand>()
            .Where(c => c.State == CommandState.Queued && c.CreatedAtUtc < cutoff)
            .OrderBy(c => c.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var provider = providers.For(MobilePlatform.Ios);
        var pushed = 0;
        foreach (var group in stale.GroupBy(c => c.DeviceId))
        {
            var device = await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == group.Key && d.Platform == MobilePlatform.Ios, cancellationToken);
            if (device is not { State: DeviceState.Enrolled or DeviceState.RemovalPending })
            {
                continue;
            }

            var first = group.First();
            await provider.DispatchCommandAsync(new CommandDispatch(first.Id, first.Type, device, first.ParametersJson, null, null), cancellationToken);
            pushed++;
        }

        return pushed;
    }

    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await db.Set<DeviceCommand>()
            .Where(c => (c.State == CommandState.PendingApproval || c.State == CommandState.Queued || c.State == CommandState.Sent) && c.ExpiresAtUtc <= now)
            .ToListAsync(cancellationToken);
        foreach (var command in due)
        {
            if (!command.ExpireIfDue(now))
            {
                continue;
            }

            var device = await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == command.DeviceId, cancellationToken);
            if (device is not null)
            {
                if (command.RequiresApproval)
                {
                    device.CancelRemoval();
                }

                DeviceLifecycle.Record(db, device, DeviceEventType.CommandFailed, $"{command.Type} expired before completing.", null, command.Id);
            }
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return due.Count;
    }

    private TimeSpan TimeToLive => TimeSpan.FromHours(options.Value.CommandTimeToLiveHours);

    private async Task DispatchAsync(MobileDevice device, DeviceCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var provider = providers.For(device.Platform);
        var enterprise = await enterprises.GetEnterpriseNameAsync(device.Platform, cancellationToken);
        if (device.Platform == MobilePlatform.Android && enterprise is null)
        {
            throw new ConflictException("The workspace is not bound to an Android Enterprise.");
        }

        CommandDispatchResult result;
        try
        {
            result = await provider.DispatchCommandAsync(
                new CommandDispatch(command.Id, command.Type, device, command.ParametersJson, enterprise, protector.Unprotect(device.ProtectedUnlockToken)),
                cancellationToken);
        }
        catch (AndroidManagementApiException ex)
        {
            Fail(device, command, ex.Message, now);
            return;
        }

        if (result.Error is not null)
        {
            Fail(device, command, result.Error, now);
            return;
        }

        if (result.StaysQueued)
        {
            return;
        }

        command.MarkSent(result.ProviderReference, now);
        if (!result.Completed)
        {
            return;
        }

        command.MarkAcknowledged(now);
        if (command.Type == DeviceCommandType.ApplyPolicy && device.PolicyId is { } policyId)
        {
            var policy = await db.Set<MobilePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == policyId, cancellationToken);
            if (policy is not null)
            {
                device.MarkPolicyApplied(policy.PublishedVersion);
            }
        }

        if (command.Type is DeviceCommandType.Retire or DeviceCommandType.Wipe)
        {
            DeviceLifecycle.CompleteRemoval(db, device, command.Type, now, command.DecidedByUserId, command.Id);
        }
        else if (command.Type != DeviceCommandType.SyncInventory)
        {
            DeviceLifecycle.Record(db, device, DeviceEventType.CommandCompleted, $"{command.Type} completed.", command.RequestedByUserId, command.Id);
        }
    }

    private static void Fail(MobileDevice device, DeviceCommand command, string error, DateTimeOffset now)
    {
        command.MarkFailed(error, now);
        device.CancelRemoval();
    }

    private async Task<CommandParameters> ValidateParametersAsync(MobileDevice device, CreateCommandRequest request, CancellationToken cancellationToken)
    {
        switch (request.Type)
        {
            case DeviceCommandType.Lock:
                if (request.Message is { Length: > MaxMessageLength })
                {
                    throw new ValidationException("message", $"The lock message must be at most {MaxMessageLength} characters.");
                }

                return new CommandParameters(Message: request.Message?.Trim());
            case DeviceCommandType.InstallApp or DeviceCommandType.RemoveApp:
                var appId = request.AppId ?? throw new ValidationException("appId", "An application is required.");
                var app = await db.Set<MobileApp>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, cancellationToken)
                    ?? throw new NotFoundException("Application", appId);
                if (app.Platform != device.Platform)
                {
                    throw new ValidationException("appId", $"{app.Name} is not an {device.Platform} application.");
                }

                if (request.Type == DeviceCommandType.InstallApp && app.StoreId is null)
                {
                    throw new ValidationException("appId", "The application needs an App Store id to be installed silently.");
                }

                return new CommandParameters(AppId: appId);
            default:
                return new CommandParameters();
        }
    }

    private async Task<MobileDevice> FindDeviceAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MobileDevice>().FirstOrDefaultAsync(d => d.Id == id, cancellationToken) ?? throw new NotFoundException("Device", id);

    private async Task<DeviceCommand> FindCommandAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<DeviceCommand>().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) ?? throw new NotFoundException("Command", id);
}
