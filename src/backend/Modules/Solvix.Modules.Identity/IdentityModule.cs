namespace Solvix.Modules.Identity;

using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Identity.Application.Access;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Emails;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Identity.Application.Mfa;
using Solvix.Modules.Identity.Application.Provisioning;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Sessions;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Modules;

public sealed class IdentityModule : IModule
{
    public string Name => "Identity";

    public Assembly Assembly => typeof(IdentityModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<IdentitySecurityOptions>()
            .Bind(configuration.GetSection(IdentitySecurityOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // PBKDF2-HMAC-SHA512 with a per-password random salt; the work factor comes from configuration.
        services.AddOptions<PasswordHasherOptions>()
            .Configure<Microsoft.Extensions.Options.IOptions<IdentitySecurityOptions>>(
                (hasher, identity) => hasher.IterationCount = identity.Value.Password.HashIterations);
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddSingleton<ISecretProtector, AesGcmSecretProtector>();

        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IPasswordService, PasswordService>();
        services.AddScoped<ILoginAttemptTracker, LoginAttemptTracker>();
        services.AddScoped<IOneTimeTokenService, OneTimeTokenService>();
        services.AddScoped<ISessionManager, SessionManager>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IAuthAuditLog, AuthAuditLog>();
        services.AddScoped<IAuditQueryService, AuditQueryService>();
        services.AddScoped<IAuthEmails, AuthEmails>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddScoped<IMfaService, MfaService>();
        services.AddScoped<IPrivilegeGuard, PrivilegeGuard>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IInvitationService, InvitationService>();
        services.AddScoped<IRoleService, RoleService>();
        services.AddScoped<ISecurityPolicyService, SecurityPolicyService>();
        services.AddScoped<IIdentityProvisioningService, IdentityProvisioningService>();
        services.AddScoped<IMemberDirectory, MemberDirectory>();

        // One resolver instance per scope behind three interfaces; cache invalidations are
        // replayed after commit by the two interceptors, which the DbContext picks up from DI.
        services.AddScoped<AccessCacheInvalidations>();
        services.AddScoped<AccessResolver>();
        services.AddScoped<IAccessResolver>(sp => sp.GetRequiredService<AccessResolver>());
        services.AddScoped<IAccessCache>(sp => sp.GetRequiredService<AccessResolver>());
        services.AddScoped<ISecurityPolicyReader>(sp => sp.GetRequiredService<AccessResolver>());
        services.AddScoped<IInterceptor, AccessCacheSaveChangesInterceptor>();
        services.AddScoped<IInterceptor, AccessCacheTransactionInterceptor>();

        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
