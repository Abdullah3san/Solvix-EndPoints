namespace Solvix.Modules.Devices.Application.Enrollment;

using Microsoft.EntityFrameworkCore;
using Solvix.Modules.Devices.Application.Credentials;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Domain;
using Solvix.SharedKernel.Abstractions;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IEnrollmentTokenService
{
    Task<IReadOnlyList<EnrollmentTokenDto>> ListAsync(CancellationToken cancellationToken);

    Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken);

    Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class EnrollmentTokenService(
    ISolvixDbContext db,
    ITenantContext tenantContext,
    ICurrentUser currentUser,
    IClock clock,
    IAuditTrail audit) : IEnrollmentTokenService
{
    public async Task<IReadOnlyList<EnrollmentTokenDto>> ListAsync(CancellationToken cancellationToken) =>
        (await db.Set<EnrollmentToken>().AsNoTracking().OrderByDescending(t => t.CreatedAtUtc).ToListAsync(cancellationToken))
            .Select(EnrollmentTokenDto.From)
            .ToList();

    public async Task<CreatedEnrollmentTokenDto> CreateAsync(CreateEnrollmentTokenRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (branchId, departmentId) = await DeviceService.ResolvePlacementAsync(db, request.DefaultBranchId, request.DefaultDepartmentId, cancellationToken);
        if (request.DefaultDeviceGroupId is { } groupId && !await db.Set<DeviceGroup>().AnyAsync(g => g.Id == groupId, cancellationToken))
        {
            throw new ValidationException("defaultDeviceGroupId", "Device group not found.");
        }

        var token = EnrollmentToken.Create(
            tenantContext.RequireTenantId(),
            request.Name,
            secretHash: "pending",
            clock.UtcNow.AddHours(request.ValidForHours),
            request.MaxUses,
            currentUser.RequireUserId(),
            branchId,
            departmentId,
            request.DefaultDeviceGroupId);
        var secret = Secrets.NewEnrollmentToken(token.Id);
        token.AttachSecretHash(Secrets.Hash(secret));
        db.Set<EnrollmentToken>().Add(token);
        audit.Record("enrollment_token.created", nameof(EnrollmentToken), token.Id, $"Enrolment token '{token.Name}' created (expires {token.ExpiresAtUtc:u}).", new { request.MaxUses, branchId, departmentId, request.DefaultDeviceGroupId });
        await db.SaveChangesAsync(cancellationToken);
        return new CreatedEnrollmentTokenDto(EnrollmentTokenDto.From(token), secret);
    }

    public async Task<EnrollmentTokenDto> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var token = await db.Set<EnrollmentToken>().FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new NotFoundException("EnrollmentToken", id);
        token.Revoke(clock.UtcNow);
        audit.Record("enrollment_token.revoked", nameof(EnrollmentToken), token.Id, $"Enrolment token '{token.Name}' revoked.");
        await db.SaveChangesAsync(cancellationToken);
        return EnrollmentTokenDto.From(token);
    }
}
