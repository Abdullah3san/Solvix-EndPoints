namespace Solvix.Modules.Identity.Application.Auth;

using FluentValidation;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256);
        RuleFor(x => x.TenantSlug).MaximumLength(63);
    }
}

public sealed class SwitchTenantRequestValidator : AbstractValidator<SwitchTenantRequest>
{
    public SwitchTenantRequestValidator()
    {
        RuleFor(x => x.TenantSlug).NotEmpty().MaximumLength(63);
    }
}
