namespace Solvix.Modules.Identity.Application.Auth;

using FluentValidation;
using Solvix.Modules.Identity.Application.Security;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(PasswordRules.MaxLength);
        RuleFor(x => x.TenantSlug).MaximumLength(63);
    }
}

public sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(x => x.MfaToken).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Code).MaximumLength(16);
        RuleFor(x => x.RecoveryCode).MaximumLength(32);
        RuleFor(x => x)
            .Must(x => string.IsNullOrWhiteSpace(x.Code) != string.IsNullOrWhiteSpace(x.RecoveryCode))
            .WithName("code")
            .WithMessage("Provide either an authenticator code or a recovery code.");
    }
}

public sealed class RefreshRequestValidator : AbstractValidator<RefreshRequest>
{
    public RefreshRequestValidator()
    {
        RuleFor(x => x.RefreshToken).NotEmpty().MaximumLength(128);
    }
}

public sealed class LogoutRequestValidator : AbstractValidator<LogoutRequest>
{
    public LogoutRequestValidator()
    {
        RuleFor(x => x.RefreshToken).MaximumLength(128);
    }
}

public sealed class SwitchTenantRequestValidator : AbstractValidator<SwitchTenantRequest>
{
    public SwitchTenantRequestValidator()
    {
        RuleFor(x => x.TenantSlug).NotEmpty().MaximumLength(63);
    }
}
