namespace Solvix.Modules.Organization.Application.Branches;

using FluentValidation;
using Solvix.Modules.Organization.Application.CompanyProfiles;
using Solvix.Modules.Organization.Domain;

internal static class BranchRules
{
    public static IRuleBuilderOptions<T, string> BranchCode<T>(this IRuleBuilder<T, string> rule) =>
        rule.NotEmpty().MaximumLength(Branch.CodeMaxLength)
            .Matches("^[A-Za-z0-9][A-Za-z0-9_-]*$")
            .WithMessage("Code may contain letters, digits, hyphens and underscores only.");
}

public sealed class CreateBranchRequestValidator : AbstractValidator<CreateBranchRequest>
{
    public CreateBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Branch.NameMaxLength);
        RuleFor(x => x.Code).BranchCode();
        RuleFor(x => x.Phone).MaximumLength(Branch.PhoneMaxLength);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrEmpty(x.Email)).MaximumLength(Branch.EmailMaxLength);
        RuleFor(x => x.TimeZone).MaximumLength(Branch.TimeZoneMaxLength)
            .Must(tz => string.IsNullOrEmpty(tz) || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            .WithMessage("Unknown time zone id.");
        RuleFor(x => x.Address!).SetValidator(new AddressDtoValidator()).When(x => x.Address is not null);
    }
}

public sealed class UpdateBranchRequestValidator : AbstractValidator<UpdateBranchRequest>
{
    public UpdateBranchRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Branch.NameMaxLength);
        RuleFor(x => x.Code).BranchCode();
        RuleFor(x => x.Phone).MaximumLength(Branch.PhoneMaxLength);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrEmpty(x.Email)).MaximumLength(Branch.EmailMaxLength);
        RuleFor(x => x.TimeZone).MaximumLength(Branch.TimeZoneMaxLength)
            .Must(tz => string.IsNullOrEmpty(tz) || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            .WithMessage("Unknown time zone id.");
        RuleFor(x => x.Address!).SetValidator(new AddressDtoValidator()).When(x => x.Address is not null);
    }
}
