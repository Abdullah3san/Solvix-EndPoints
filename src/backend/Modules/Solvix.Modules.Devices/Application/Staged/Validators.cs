namespace Solvix.Modules.Devices.Application.Staged;

using FluentValidation;

public sealed class CreateStagedComputerRequestValidator : AbstractValidator<CreateStagedComputerRequest>
{
    public CreateStagedComputerRequestValidator()
    {
        RuleFor(x => x.Hostname).NotEmpty().MaximumLength(Domain.StagedComputer.HostnameMaxLength)
            .Must(Domain.StagedComputer.IsValidHostname).WithMessage("Use letters, digits, dots, hyphens and underscores only (e.g. FIN-LT-0142).");
    }
}

public sealed class ImportStagedComputersRequestValidator : AbstractValidator<ImportStagedComputersRequest>
{
    public const int MaxEntries = 1000;

    public ImportStagedComputersRequestValidator()
    {
        RuleFor(x => x.Hostnames).NotNull().Must(h => h is { Count: > 0 }).WithMessage("Provide at least one hostname.")
            .Must(h => h is null || h.Count <= MaxEntries).WithMessage($"Import at most {MaxEntries} computers at a time.");
    }
}

public sealed class RemoveStagedComputersRequestValidator : AbstractValidator<RemoveStagedComputersRequest>
{
    public RemoveStagedComputersRequestValidator()
    {
        RuleFor(x => x.Ids).NotNull().Must(ids => ids is { Count: > 0 and <= 1000 }).WithMessage("Provide between 1 and 1000 ids.");
    }
}
