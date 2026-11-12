namespace Solvix.Modules.Tenancy.Application.Registration;

using FluentValidation;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Tenancy;

public sealed class RegisterTenantRequestValidator : AbstractValidator<RegisterTenantRequest>
{
    public RegisterTenantRequestValidator()
    {
        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(x => x.Slug)
            .Must(s => s is null || Slug.IsValid(s.Trim().ToLowerInvariant()))
            .WithMessage($"Slug must be {Slug.MinLength}-{Slug.MaxLength} lowercase letters, digits or hyphens and must not be reserved.");
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.OwnerDisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OwnerPassword).Password();
    }
}
