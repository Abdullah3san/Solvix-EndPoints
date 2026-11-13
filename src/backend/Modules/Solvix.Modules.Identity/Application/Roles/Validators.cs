namespace Solvix.Modules.Identity.Application.Roles;

using FluentValidation;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;

public sealed class SaveRoleRequestValidator : AbstractValidator<SaveRoleRequest>
{
    public SaveRoleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Role.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(Role.DescriptionMaxLength);
        RuleFor(x => x.Permissions)
            .NotNull()
            .Must(p => p is { Count: >= 1 }).WithMessage("A role needs at least one permission.")
            .Must(p => p is null || p.All(PermissionCatalog.IsKnown)).WithMessage("One or more permissions are not recognised.");
    }
}
