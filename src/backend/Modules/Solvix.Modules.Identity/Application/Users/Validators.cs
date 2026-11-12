namespace Solvix.Modules.Identity.Application.Users;

using FluentValidation;

public sealed class SetUserRolesRequestValidator : AbstractValidator<SetUserRolesRequest>
{
    public SetUserRolesRequestValidator()
    {
        RuleFor(x => x.RoleIds)
            .NotNull()
            .Must(ids => ids is { Count: >= 1 and <= 20 }).WithMessage("Assign between 1 and 20 roles.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("Each role can only be assigned once.");
    }
}
