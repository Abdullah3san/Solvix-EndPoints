namespace Solvix.Modules.Tenancy.Application.Platform;

using FluentValidation;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Tenancy;

public sealed class CreatePlatformTenantRequestValidator : AbstractValidator<CreatePlatformTenantRequest>
{
    public CreatePlatformTenantRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(x => x.Slug)
            .Must(s => string.IsNullOrWhiteSpace(s) || Slug.IsValid(s.Trim().ToLowerInvariant()))
            .WithMessage($"Workspace id must be {Slug.MinLength}-{Slug.MaxLength} lowercase letters, digits or hyphens and must not be reserved.");
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.OwnerDisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OwnerPassword).Password();
    }
}
