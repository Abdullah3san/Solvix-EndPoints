namespace Solvix.Modules.Tenancy.Application.Management;

using FluentValidation;
using Solvix.Modules.Tenancy.Domain;

public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Tenant.NameMaxLength);
    }
}
