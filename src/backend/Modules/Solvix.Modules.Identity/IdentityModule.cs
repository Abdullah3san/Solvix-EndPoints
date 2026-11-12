namespace Solvix.Modules.Identity;

using System.Reflection;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Members;
using Solvix.Modules.Identity.Application.Provisioning;
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

        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddScoped<ITokenService, JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserProvisioningService, UserProvisioningService>();
        services.AddScoped<IMemberService, MemberService>();
        services.AddScoped<CachedMembershipLookup>();
        services.AddScoped<IMembershipLookup>(sp => sp.GetRequiredService<CachedMembershipLookup>());
        services.AddScoped<IMembershipCache>(sp => sp.GetRequiredService<CachedMembershipLookup>());
        services.AddValidatorsFromAssembly(Assembly, includeInternalTypes: true);
    }
}
