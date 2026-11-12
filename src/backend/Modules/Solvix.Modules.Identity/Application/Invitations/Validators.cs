namespace Solvix.Modules.Identity.Application.Invitations;

using FluentValidation;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Domain;

public sealed class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(User.EmailMaxLength);
        RuleFor(x => x.DisplayName).MaximumLength(User.DisplayNameMaxLength);
        RuleFor(x => x.RoleIds)
            .NotNull()
            .Must(ids => ids is { Count: >= 1 and <= 20 }).WithMessage("Choose between 1 and 20 roles.")
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("Each role can only be chosen once.");
    }
}

public sealed class InvitationTokenRequestValidator : AbstractValidator<InvitationTokenRequest>
{
    public InvitationTokenRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(128);
    }
}

public sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(128);
        RuleFor(x => x.DisplayName).MaximumLength(User.DisplayNameMaxLength);
        RuleFor(x => x.Password).MaximumLength(PasswordRules.MaxLength);
    }
}
