namespace Solvix.Modules.Tenancy.Application.Platform;

using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Tenancy.Application.Registration;
using Solvix.Modules.Tenancy.Configuration;
using Solvix.SharedKernel.Persistence;
using Solvix.SharedKernel.Tenancy;

public interface IPlatformOperatorSeeder
{
    Task RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Makes the database agree with deployment configuration about who the
/// platform operator is. Runs once at startup. Configuration is the single
/// source of truth: the configured e-mail is the operator and nobody else is.
/// </summary>
internal sealed partial class PlatformOperatorSeeder(
    ISolvixDbContext db,
    ITenantContextMutator tenantContextMutator,
    ITenantProvisioner registration,
    IOptions<PlatformOptions> options,
    ILogger<PlatformOperatorSeeder> logger) : IPlatformOperatorSeeder
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var platformScope = tenantContextMutator.BeginPlatformScope();
        var settings = options.Value.Operator;
        var email = settings.Email?.Trim();
        var normalized = string.IsNullOrEmpty(email) ? null : User.NormalizeEmail(email);

        // Revoke first, so removing or changing the configured e-mail always takes effect.
        var stale = await db.Set<User>().Where(u => u.IsPlatformAdmin && u.NormalizedEmail != normalized).ToListAsync(cancellationToken);
        foreach (var user in stale)
        {
            user.SetPlatformAdmin(false);
            LogRevoked(logger, user.Id);
        }

        await db.SaveChangesAsync(cancellationToken);

        if (normalized is null)
        {
            LogNotConfigured(logger);
            return;
        }

        var account = await db.Set<User>().FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, cancellationToken);
        if (account is null)
        {
            var password = settings.InitialPassword;
            if (string.IsNullOrEmpty(password) || !IsAcceptable(password))
            {
                LogCannotCreate(logger, PasswordPolicy.MinLength);
                return;
            }

            // New account: it gets its own workspace so that it can sign in.
            var created = await registration.CreateAsync(
                new RegisterTenantRequest(settings.WorkspaceName, null, email!, settings.DisplayName, password),
                cancellationToken,
                uniqueSlug: true);
            account = await db.Set<User>().FirstAsync(u => u.Id == created.Owner.Id, cancellationToken);
            LogCreated(logger, account.Id);
        }
        else if (!await db.Set<TenantMembership>().IgnoreQueryFilters().AnyAsync(m => m.UserId == account.Id && m.IsActive, cancellationToken))
        {
            // Existing account without any workspace cannot sign in: give it one.
            await registration.CreateForExistingOwnerAsync(settings.WorkspaceName, account, cancellationToken);
        }

        if (!account.IsPlatformAdmin || !account.IsActive)
        {
            account.SetPlatformAdmin(true);
            account.Activate();
            await db.SaveChangesAsync(cancellationToken);
            LogGranted(logger, account.Id);
        }

    }

    private static bool IsAcceptable(string password)
    {
        var validator = new InlineValidator<string>();
        validator.RuleFor(p => p).Password();
        return validator.Validate(password).IsValid;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No platform operator is configured (Solvix:Platform:Operator:Email). Nobody can create or suspend workspaces.")]
    private static partial void LogNotConfigured(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "The configured platform operator has no account and Solvix:Platform:Operator:InitialPassword is missing or too weak (at least {MinLength} characters with a letter and a digit). No operator account was created.")]
    private static partial void LogCannotCreate(ILogger logger, int minLength);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform operator account {UserId} created from configuration")]
    private static partial void LogCreated(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform operator rights granted to user {UserId}")]
    private static partial void LogGranted(ILogger logger, Guid userId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Platform operator rights revoked from user {UserId}")]
    private static partial void LogRevoked(ILogger logger, Guid userId);
}
