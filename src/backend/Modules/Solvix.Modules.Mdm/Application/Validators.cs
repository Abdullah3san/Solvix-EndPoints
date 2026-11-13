namespace Solvix.Modules.Mdm.Application;

using FluentValidation;
using Solvix.Modules.Mdm.Domain;

public sealed class CreatePolicyRequestValidator : AbstractValidator<CreatePolicyRequest>
{
    public CreatePolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MobilePolicy.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(MobilePolicy.DescriptionMaxLength);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Ownership).IsInEnum();
    }
}

public sealed class UpdatePolicyRequestValidator : AbstractValidator<UpdatePolicyRequest>
{
    public UpdatePolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MobilePolicy.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(MobilePolicy.DescriptionMaxLength);
    }
}

public sealed class CreateAppRequestValidator : AbstractValidator<CreateAppRequest>
{
    public CreateAppRequestValidator()
    {
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MobileApp.NameMaxLength);
        RuleFor(x => x.Identifier).NotEmpty().MaximumLength(MobileApp.IdentifierMaxLength)
            .Matches(@"^[A-Za-z0-9][A-Za-z0-9._-]*$").WithMessage("Identifier may contain letters, digits, dots, hyphens and underscores only.");
        RuleFor(x => x.StoreId).GreaterThan(0).When(x => x.StoreId is not null);
    }
}

public sealed class UpdateAppRequestValidator : AbstractValidator<UpdateAppRequest>
{
    public UpdateAppRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(MobileApp.NameMaxLength);
        RuleFor(x => x.StoreId).GreaterThan(0).When(x => x.StoreId is not null);
    }
}

public sealed class CreateEnrollmentRequestValidator : AbstractValidator<CreateEnrollmentRequest>
{
    public CreateEnrollmentRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(EnrollmentToken.NameMaxLength);
        RuleFor(x => x.Platform).IsInEnum();
        RuleFor(x => x.Mode).IsInEnum();
        RuleFor(x => x.Ownership).IsInEnum();
        RuleFor(x => x.PolicyId).NotEmpty();
    }
}

public sealed class CreateCommandRequestValidator : AbstractValidator<CreateCommandRequest>
{
    public CreateCommandRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum();
        RuleFor(x => x.Reason).MaximumLength(DeviceCommand.ReasonMaxLength);
        RuleFor(x => x.Reason).NotEmpty()
            .When(x => MdmCapabilities.RequiresApproval(x.Type))
            .WithMessage("A reason is required for destructive commands.");
    }
}

public sealed class CommandDecisionRequestValidator : AbstractValidator<CommandDecisionRequest>
{
    public CommandDecisionRequestValidator()
    {
        RuleFor(x => x.Note).MaximumLength(DeviceCommand.ReasonMaxLength);
    }
}

public sealed class StartSignupRequestValidator : AbstractValidator<StartSignupRequest>
{
    public StartSignupRequestValidator()
    {
        RuleFor(x => x.CallbackUrl).NotEmpty().MaximumLength(2000)
            .Must(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            .WithMessage("The callback URL must be an absolute http(s) URL.");
    }
}

public sealed class CompleteSignupRequestValidator : AbstractValidator<CompleteSignupRequest>
{
    public CompleteSignupRequestValidator()
    {
        RuleFor(x => x.EnterpriseToken).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.DisplayName).MaximumLength(200);
    }
}
