namespace Solvix.Modules.Identity.Application.Provisioning;

using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Tenancy;

/// <param name="VerificationToken">Secret for the confirmation e-mail; send it with <see cref="IIdentityProvisioningService.SendVerificationEmail"/> after the transaction commits.</param>
/// <param name="Session">Set only when e-mail verification is not required and the owner was signed in straight away.</param>
public sealed record ProvisionedOwner(User User, string VerificationToken, AuthResponse? Session);

/// <summary>
/// Sets up identity for a brand-new tenant on behalf of the Tenancy module:
/// the built-in roles, the default security policy and the owner account.
/// Callers own the surrounding transaction and run in platform scope.
/// </summary>
public interface IIdentityProvisioningService
{
    Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the tenant's roles and policy and its first user as Super
    /// Administrator. <paramref name="passwordField"/> names the request property
    /// a password error is reported against.
    /// </summary>
    Task<ProvisionedOwner> CreateTenantOwnerAsync(
        TenantDescriptor tenant,
        string email,
        string displayName,
        string password,
        string passwordField,
        CancellationToken cancellationToken);

    void SendVerificationEmail(ProvisionedOwner owner);

    bool EmailVerificationRequired { get; }
}
