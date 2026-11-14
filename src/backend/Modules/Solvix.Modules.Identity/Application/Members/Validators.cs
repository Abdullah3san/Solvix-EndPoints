namespace Solvix.Modules.Identity.Application.Members;

using FluentValidation;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.SharedKernel.Identity;

public sealed class AddMemberRequestValidator : AbstractValidator<AddMemberRequest>
{
    public AddMemberRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Role).IsInEnum().NotEqual(TenantRole.Owner)
            .WithMessage("Ownership is transferred explicitly; new members cannot be created as Owner.");
        When(x => x.Password is not null, () => RuleFor(x => x.Password!).Password());
    }
}

public sealed class ChangeMemberRoleRequestValidator : AbstractValidator<ChangeMemberRoleRequest>
{
    public ChangeMemberRoleRequestValidator()
    {
        RuleFor(x => x.Role).IsInEnum();
    }
}
