namespace Solvix.Modules.Tenancy.Application.Registration;

using FluentValidation;
using Microsoft.Extensions.Options;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Configuration;
using Solvix.Modules.Tenancy.Domain;
using Solvix.SharedKernel.Tenancy;

public sealed class RegisterTenantRequestValidator : AbstractValidator<RegisterTenantRequest>
{
    public RegisterTenantRequestValidator(IOptions<IdentitySecurityOptions> identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // A new workspace has no policy of its own yet: the platform baseline applies.
        var rules = PasswordRules.From(identity.Value.Baseline());

        RuleFor(x => x.CompanyName).NotEmpty().MaximumLength(Tenant.NameMaxLength);
        RuleFor(x => x.Slug)
            .Must(s => s is null || Slug.IsValid(s.Trim().ToLowerInvariant()))
            .WithMessage($"Slug must be {Slug.MinLength}-{Slug.MaxLength} lowercase letters, digits or hyphens and must not be reserved.");
        RuleFor(x => x.OwnerEmail).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.OwnerDisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.OwnerPassword).Custom((password, context) =>
        {
            var request = context.InstanceToValidate;
            foreach (var error in PasswordPolicy.Validate(password, rules, request.OwnerEmail, request.OwnerDisplayName))
            {
                context.AddFailure(error);
            }
        });
    }
}
