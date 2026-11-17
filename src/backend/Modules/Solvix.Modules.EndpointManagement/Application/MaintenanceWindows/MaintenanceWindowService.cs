namespace Solvix.Modules.EndpointManagement.Application.MaintenanceWindows;

using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Solvix.Modules.EndpointManagement.Domain.Configuration;
using Solvix.Modules.EndpointManagement.Domain.Deployments;
using Solvix.Modules.EndpointManagement.Domain.MaintenanceWindows;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public sealed record MaintenanceWindowDto(
    Guid Id,
    string Name,
    string? Description,
    MaintenanceWindowRecurrence Recurrence,
    string TimeZoneId,
    WeekDays DaysOfWeek,
    TimeOnly StartTime,
    int DurationMinutes,
    DateTimeOffset? OnceStartUtc,
    DateTimeOffset? OnceEndUtc,
    bool IsEnabled,
    bool IsOpenNow,
    DateTimeOffset? CurrentOccurrenceEndsUtc,
    DateTimeOffset? NextOccurrenceStartsUtc)
{
    public static MaintenanceWindowDto From(MaintenanceWindow w, DateTimeOffset now)
    {
        var current = w.OccurrenceAt(now);
        return new MaintenanceWindowDto(
            w.Id, w.Name, w.Description, w.Recurrence, w.TimeZoneId, w.DaysOfWeek, w.StartTime, w.DurationMinutes,
            w.OnceStartUtc, w.OnceEndUtc, w.IsEnabled, current is not null, current?.EndUtc, w.NextOccurrenceAfter(now)?.StartUtc);
    }
}

public sealed record SaveMaintenanceWindowRequest(
    string Name,
    string? Description,
    MaintenanceWindowRecurrence Recurrence,
    string? TimeZoneId,
    WeekDays DaysOfWeek,
    TimeOnly? StartTime,
    int? DurationMinutes,
    DateTimeOffset? OnceStartUtc,
    DateTimeOffset? OnceEndUtc,
    bool IsEnabled = true);

public sealed class SaveMaintenanceWindowRequestValidator : AbstractValidator<SaveMaintenanceWindowRequest>
{
    public SaveMaintenanceWindowRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MaintenanceWindow.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(MaintenanceWindow.DescriptionMaxLength);
        RuleFor(x => x.Recurrence).IsInEnum();
        When(x => x.Recurrence == MaintenanceWindowRecurrence.Weekly, () =>
        {
            RuleFor(x => x.TimeZoneId).NotEmpty()
                .Must(tz => tz is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)).WithMessage("Unknown time zone id.");
            RuleFor(x => x.DaysOfWeek).Must(d => (d & WeekDays.Everyday) != WeekDays.None).WithMessage("Select at least one day.");
            RuleFor(x => x.StartTime).NotNull();
            RuleFor(x => x.DurationMinutes).NotNull().InclusiveBetween(MaintenanceWindow.MinDurationMinutes, MaintenanceWindow.MaxDurationMinutes);
        });
        When(x => x.Recurrence == MaintenanceWindowRecurrence.Once, () =>
        {
            RuleFor(x => x.OnceStartUtc).NotNull();
            RuleFor(x => x.OnceEndUtc).NotNull().GreaterThan(x => x.OnceStartUtc).WithMessage("The window must end after it starts.");
        });
    }
}

public interface IMaintenanceWindowService
{
    Task<IReadOnlyList<MaintenanceWindowDto>> ListAsync(CancellationToken cancellationToken);

    Task<MaintenanceWindowDto> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<MaintenanceWindowDto> CreateAsync(SaveMaintenanceWindowRequest request, CancellationToken cancellationToken);

    Task<MaintenanceWindowDto> UpdateAsync(Guid id, SaveMaintenanceWindowRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class MaintenanceWindowService(ISolvixDbContext db, ITenantContext tenantContext, IClock clock, IAuditTrail audit) : IMaintenanceWindowService
{
    public async Task<IReadOnlyList<MaintenanceWindowDto>> ListAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        return (await db.Set<MaintenanceWindow>().AsNoTracking().OrderBy(w => w.Name).ToListAsync(cancellationToken))
            .Select(w => MaintenanceWindowDto.From(w, now)).ToList();
    }

    public async Task<MaintenanceWindowDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        MaintenanceWindowDto.From(await FindAsync(id, cancellationToken), clock.UtcNow);

    public async Task<MaintenanceWindowDto> CreateAsync(SaveMaintenanceWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureUniqueNameAsync(request.Name, null, cancellationToken);
        var tenantId = tenantContext.RequireTenantId();
        var window = request.Recurrence == MaintenanceWindowRecurrence.Once
            ? MaintenanceWindow.CreateOnce(tenantId, request.Name, request.OnceStartUtc!.Value, request.OnceEndUtc!.Value)
            : MaintenanceWindow.CreateWeekly(tenantId, request.Name, request.TimeZoneId!, request.DaysOfWeek, request.StartTime!.Value, request.DurationMinutes!.Value);
        window.Rename(request.Name, request.Description);
        if (!request.IsEnabled)
        {
            window.Disable();
        }

        db.Set<MaintenanceWindow>().Add(window);
        audit.Record("maintenance_window.created", nameof(MaintenanceWindow), window.Id, $"Maintenance window '{window.Name}' created.", request);
        await db.SaveChangesAsync(cancellationToken);
        return MaintenanceWindowDto.From(window, clock.UtcNow);
    }

    public async Task<MaintenanceWindowDto> UpdateAsync(Guid id, SaveMaintenanceWindowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var window = await FindAsync(id, cancellationToken);
        await EnsureUniqueNameAsync(request.Name, id, cancellationToken);
        window.Rename(request.Name, request.Description);
        if (request.Recurrence == MaintenanceWindowRecurrence.Once)
        {
            window.SetOnce(request.OnceStartUtc!.Value, request.OnceEndUtc!.Value);
        }
        else
        {
            window.SetWeekly(request.TimeZoneId!, request.DaysOfWeek, request.StartTime!.Value, request.DurationMinutes!.Value);
        }

        if (request.IsEnabled)
        {
            window.Enable();
        }
        else
        {
            window.Disable();
        }

        audit.Record("maintenance_window.updated", nameof(MaintenanceWindow), id, $"Maintenance window '{window.Name}' updated.", request);
        await db.SaveChangesAsync(cancellationToken);
        return MaintenanceWindowDto.From(window, clock.UtcNow);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var window = await FindAsync(id, cancellationToken);
        var usedByDeployment = await db.Set<PatchDeployment>().AnyAsync(
            d => d.MaintenanceWindowId == id && d.Status != DeploymentStatus.Completed && d.Status != DeploymentStatus.CompletedWithFailures && d.Status != DeploymentStatus.Cancelled,
            cancellationToken);
        var usedByAssignment = await db.Set<ConfigurationAssignment>().AnyAsync(a => a.MaintenanceWindowId == id, cancellationToken);
        var usedByRestart = await db.Set<DeviceRestartRequest>().AnyAsync(r => r.MaintenanceWindowId == id && (r.Status == RestartRequestStatus.Pending || r.Status == RestartRequestStatus.Dispatched), cancellationToken);
        if (usedByDeployment || usedByAssignment || usedByRestart)
        {
            throw new ConflictException("The maintenance window is used by an active deployment, a policy assignment or a pending restart. Disable it or change those first.");
        }

        // Finished deployments keep a historical reference; detach is not possible with a
        // restrictive FK, so only windows never referenced by a deployment can be deleted.
        if (await db.Set<PatchDeployment>().AnyAsync(d => d.MaintenanceWindowId == id, cancellationToken)
            || await db.Set<DeviceRestartRequest>().AnyAsync(r => r.MaintenanceWindowId == id, cancellationToken))
        {
            throw new ConflictException("The maintenance window is referenced by deployment history. Disable it instead.");
        }

        db.Set<MaintenanceWindow>().Remove(window);
        audit.Record("maintenance_window.deleted", nameof(MaintenanceWindow), id, $"Maintenance window '{window.Name}' deleted.");
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<MaintenanceWindow> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Set<MaintenanceWindow>().FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
        ?? throw new NotFoundException("MaintenanceWindow", id);

    private async Task EnsureUniqueNameAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        var trimmed = name.Trim();
        if (await db.Set<MaintenanceWindow>().AnyAsync(w => w.Name == trimmed && w.Id != exceptId, cancellationToken))
        {
            throw new ConflictException($"A maintenance window named '{trimmed}' already exists.");
        }
    }
}
