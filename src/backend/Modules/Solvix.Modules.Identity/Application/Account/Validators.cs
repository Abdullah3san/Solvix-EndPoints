namespace Solvix.Modules.Identity.Application.Account;

using FluentValidation;
using Solvix.Modules.Identity.Application.Security;

// Password *strength* is checked by the password service against the rules that
// apply to the account; validators here only bound the input.
public sealed class VerifyEmailRequestValidator : AbstractValidator<VerifyEmailRequest>
{
    public VerifyEmailRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(128);
    }
}

public sealed class EmailRequestValidator : AbstractValidator<EmailRequest>
{
    public EmailRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
    }
}

public sealed class ResetPasswordRequestValidator : AbstractValidator<ResetPasswordRequest>
{
    public ResetPasswordRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(128);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.NewPassword).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

public sealed class PasswordConfirmationRequestValidator : AbstractValidator<PasswordConfirmationRequest>
{
    public PasswordConfirmationRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
    }
}

public sealed class MfaConfirmRequestValidator : AbstractValidator<MfaConfirmRequest>
{
    public MfaConfirmRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(16);
    }
}

public sealed class MfaProtectedRequestValidator : AbstractValidator<MfaProtectedRequest>
{
    public MfaProtectedRequestValidator()
    {
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(32);
    }
}
