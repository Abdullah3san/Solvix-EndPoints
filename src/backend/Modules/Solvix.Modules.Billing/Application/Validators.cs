namespace Solvix.Modules.Billing.Application;

using FluentValidation;
using Solvix.Modules.Billing.Domain;

public sealed class ChangePlanRequestValidator : AbstractValidator<ChangePlanRequest>
{
    public ChangePlanRequestValidator()
    {
        RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(32).Matches("^[a-z0-9-]+$");
        RuleFor(x => x.Interval).IsInEnum();
    }
}

public sealed class UpdateBillingProfileRequestValidator : AbstractValidator<UpdateBillingProfileRequest>
{
    public UpdateBillingProfileRequestValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(BillingProfile.TextMaxLength);
        RuleFor(x => x.BillingEmail).NotEmpty().MaximumLength(254).EmailAddress();
        RuleFor(x => x.AddressLine1).MaximumLength(BillingProfile.TextMaxLength);
        RuleFor(x => x.AddressLine2).MaximumLength(BillingProfile.TextMaxLength);
        RuleFor(x => x.City).MaximumLength(100);
        RuleFor(x => x.Region).MaximumLength(100);
        RuleFor(x => x.PostalCode).MaximumLength(20);
        RuleFor(x => x.CountryCode).Length(2).Matches("^[A-Za-z]{2}$").When(x => !string.IsNullOrEmpty(x.CountryCode));
        RuleFor(x => x.TaxId).MaximumLength(50);
    }
}

public sealed class OperatorConfigureSubscriptionRequestValidator : AbstractValidator<OperatorConfigureSubscriptionRequest>
{
    public OperatorConfigureSubscriptionRequestValidator()
    {
        RuleFor(x => x.PlanCode).NotEmpty().MaximumLength(32).Matches("^[a-z0-9-]+$");
        RuleFor(x => x.Interval).IsInEnum();
        RuleFor(x => x.DeviceLimit).InclusiveBetween(1, 1_000_000).When(x => x.DeviceLimit is not null);
        RuleFor(x => x.UnitPriceMinor).InclusiveBetween(0, 1_000_000_000).When(x => x.UnitPriceMinor is not null);
    }
}

public sealed class OperatorMarkPaidRequestValidator : AbstractValidator<OperatorMarkPaidRequest>
{
    public OperatorMarkPaidRequestValidator()
    {
        RuleFor(x => x.Reference).NotEmpty().MaximumLength(200);
    }
}

public sealed class OperatorSuspendRequestValidator : AbstractValidator<OperatorSuspendRequest>
{
    public OperatorSuspendRequestValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(500);
    }
}

public sealed class OperatorTaxRateRequestValidator : AbstractValidator<OperatorTaxRateRequest>
{
    public OperatorTaxRateRequestValidator()
    {
        RuleFor(x => x.TaxRateBasisPoints).InclusiveBetween(0, 10_000).When(x => x.TaxRateBasisPoints is not null);
    }
}
