namespace Solvix.Modules.Organization.Application.Departments;

using FluentValidation;
using Solvix.Modules.Organization.Domain;

public sealed class CreateDepartmentRequestValidator : AbstractValidator<CreateDepartmentRequest>
{
    public CreateDepartmentRequestValidator()
    {
        RuleFor(x => x.BranchId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Department.NameMaxLength);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(Department.CodeMaxLength).Matches("^[A-Za-z0-9][A-Za-z0-9_-]*$");
        RuleFor(x => x.Description).MaximumLength(Department.DescriptionMaxLength);
    }
}

public sealed class UpdateDepartmentRequestValidator : AbstractValidator<UpdateDepartmentRequest>
{
    public UpdateDepartmentRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(Department.NameMaxLength);
        RuleFor(x => x.Code).NotEmpty().MaximumLength(Department.CodeMaxLength).Matches("^[A-Za-z0-9][A-Za-z0-9_-]*$");
        RuleFor(x => x.Description).MaximumLength(Department.DescriptionMaxLength);
    }
}
