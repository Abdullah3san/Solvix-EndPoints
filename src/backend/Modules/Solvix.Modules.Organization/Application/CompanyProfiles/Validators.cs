namespace Solvix.Modules.Organization.Application.CompanyProfiles;

using FluentValidation;
using Solvix.Modules.Organization.Domain;

public sealed class AddressDtoValidator : AbstractValidator<AddressDto>
{
    public AddressDtoValidator()
    {
        RuleFor(a => a.Line1).MaximumLength(Address.LineMaxLength);
        RuleFor(a => a.Line2).MaximumLength(Address.LineMaxLength);
        RuleFor(a => a.City).MaximumLength(Address.CityMaxLength);
        RuleFor(a => a.Region).MaximumLength(Address.RegionMaxLength);
        RuleFor(a => a.PostalCode).MaximumLength(Address.PostalCodeMaxLength);
        RuleFor(a => a.CountryCode).Length(2).When(a => !string.IsNullOrEmpty(a.CountryCode));
    }
}

public sealed class UpdateCompanyProfileRequestValidator : AbstractValidator<UpdateCompanyProfileRequest>
{
    public UpdateCompanyProfileRequestValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(CompanyProfile.NameMaxLength);
        RuleFor(x => x.TradeName).MaximumLength(CompanyProfile.NameMaxLength);
        RuleFor(x => x.TaxId).MaximumLength(CompanyProfile.TaxIdMaxLength);
        RuleFor(x => x.RegistrationNumber).MaximumLength(CompanyProfile.TaxIdMaxLength);
        RuleFor(x => x.Industry).MaximumLength(CompanyProfile.IndustryMaxLength);
        RuleFor(x => x.Website).MaximumLength(CompanyProfile.WebsiteMaxLength)
            .Must(w => string.IsNullOrEmpty(w) || Uri.TryCreate(w, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .WithMessage("Website must be an absolute http(s) URL.");
        RuleFor(x => x.ContactEmail).EmailAddress().When(x => !string.IsNullOrEmpty(x.ContactEmail)).MaximumLength(CompanyProfile.EmailMaxLength);
        RuleFor(x => x.ContactPhone).MaximumLength(CompanyProfile.PhoneMaxLength);
        RuleFor(x => x.TimeZone).MaximumLength(CompanyProfile.TimeZoneMaxLength)
            .Must(tz => string.IsNullOrEmpty(tz) || TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _))
            .WithMessage("Unknown time zone id.");
        RuleFor(x => x.Address!).SetValidator(new AddressDtoValidator()).When(x => x.Address is not null);
    }
}
