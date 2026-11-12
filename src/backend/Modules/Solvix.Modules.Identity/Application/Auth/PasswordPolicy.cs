namespace Solvix.Modules.Identity.Application.Auth;

using FluentValidation;

public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 256;

    public static IRuleBuilderOptions<T, string> Password<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty()
            .MinimumLength(MinLength).WithMessage($"Password must be at least {MinLength} characters.")
            .MaximumLength(MaxLength)
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit))
            .WithMessage("Password must contain at least one letter and one digit.");
}
