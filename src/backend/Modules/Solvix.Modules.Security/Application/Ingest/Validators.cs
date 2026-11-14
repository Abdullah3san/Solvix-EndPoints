namespace Solvix.Modules.Security.Application.Ingest;

using FluentValidation;
using Solvix.Modules.Security.Evaluation;

/// <summary>
/// Everything an agent sends is untrusted input: sizes are capped, counts are capped and ranges are checked
/// before any of it reaches the evaluator or the database.
/// </summary>
public sealed class PostureReportValidator : AbstractValidator<PostureReport>
{
    public PostureReportValidator()
    {
        RuleFor(r => r.SchemaVersion).Equal(PostureReport.CurrentSchemaVersion).WithMessage($"Only schema version {PostureReport.CurrentSchemaVersion} is supported.");
        RuleFor(r => r.CollectedAtUtc).NotEmpty();
        RuleFor(r => r.Hostname).MaximumLength(200);
        RuleFor(r => r.OsCaption).MaximumLength(200);
        RuleFor(r => r.OsVersion).MaximumLength(200);
        RuleFor(r => r.AgentVersion).MaximumLength(64);

        When(r => r.Defender is not null, () =>
        {
            RuleFor(r => r.Defender!.AMRunningMode).MaximumLength(64);
            RuleFor(r => r.Defender!.SignatureVersion).MaximumLength(64);
            RuleFor(r => r.Defender!.SignatureAgeDays).InclusiveBetween(0, 100_000).When(r => r.Defender!.SignatureAgeDays is not null);
            RuleFor(r => r.Defender!.QuickScanAgeDays).InclusiveBetween(0, 100_000).When(r => r.Defender!.QuickScanAgeDays is not null);
            RuleFor(r => r.Defender!.ActiveThreatCount).InclusiveBetween(0, 1_000_000).When(r => r.Defender!.ActiveThreatCount is not null);
        });

        When(r => r.Firewall?.Profiles is not null, () =>
        {
            RuleFor(r => r.Firewall!.Profiles!).Must(p => p.Count <= 8).WithMessage("At most 8 firewall profiles.");
            RuleForEach(r => r.Firewall!.Profiles!).ChildRules(p =>
            {
                p.RuleFor(x => x.Name).NotEmpty().MaximumLength(32);
                p.RuleFor(x => x.DefaultInboundAction).MaximumLength(32);
            });
        });

        When(r => r.BitLocker?.Volumes is not null, () =>
        {
            RuleFor(r => r.BitLocker!.Volumes!).Must(v => v.Count <= PostureReport.MaxVolumes).WithMessage($"At most {PostureReport.MaxVolumes} volumes.");
            RuleForEach(r => r.BitLocker!.Volumes!).ChildRules(v =>
            {
                v.RuleFor(x => x.MountPoint).NotEmpty().MaximumLength(64);
                v.RuleFor(x => x.VolumeType).MaximumLength(32);
                v.RuleFor(x => x.EncryptionMethod).MaximumLength(64);
                v.RuleFor(x => x.VolumeStatus).MaximumLength(64);
                v.RuleFor(x => x.EncryptionPercentage).InclusiveBetween(0, 100).When(x => x.EncryptionPercentage is not null);
            });
        });

        When(r => r.LocalAdmins?.Members is not null, () =>
        {
            RuleFor(r => r.LocalAdmins!.Members!).Must(m => m.Count <= PostureReport.MaxAdminMembers).WithMessage($"At most {PostureReport.MaxAdminMembers} administrators.");
            RuleForEach(r => r.LocalAdmins!.Members!).ChildRules(m =>
            {
                m.RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
                m.RuleFor(x => x.ObjectClass).MaximumLength(32);
                m.RuleFor(x => x.Sid).MaximumLength(256);
            });
        });

        When(r => r.Software is not null, () =>
        {
            RuleFor(r => r.Software!).Must(s => s.Count <= PostureReport.MaxSoftwareItems).WithMessage($"At most {PostureReport.MaxSoftwareItems} software items.");
            RuleForEach(r => r.Software!).ChildRules(s =>
            {
                s.RuleFor(x => x.Name).NotEmpty().MaximumLength(300);
                s.RuleFor(x => x.Version).MaximumLength(64);
                s.RuleFor(x => x.Publisher).MaximumLength(200);
            });
        });

        When(r => r.Events is not null, () =>
        {
            RuleFor(r => r.Events!).Must(e => e.Count <= PostureReport.MaxEvents).WithMessage($"At most {PostureReport.MaxEvents} events per report.");
            RuleForEach(r => r.Events!).ChildRules(e =>
            {
                e.RuleFor(x => x.Log).NotEmpty().MaximumLength(100);
                e.RuleFor(x => x.RecordId).GreaterThanOrEqualTo(0);
                e.RuleFor(x => x.EventId).InclusiveBetween(0, 65535);
                e.RuleFor(x => x.Provider).MaximumLength(200);
                e.RuleFor(x => x.Summary).MaximumLength(1000);
                e.RuleFor(x => x.TargetGroup).MaximumLength(100);
            });
        });

        When(r => r.Errors is not null, () =>
        {
            RuleFor(r => r.Errors!).Must(e => e.Count <= PostureReport.MaxErrors).WithMessage($"At most {PostureReport.MaxErrors} collection errors.");
            RuleForEach(r => r.Errors!).ChildRules(e =>
            {
                e.RuleFor(x => x.Section).NotEmpty().MaximumLength(64);
                e.RuleFor(x => x.Message).MaximumLength(500);
            });
        });
    }
}

public sealed class CommandResultRequestValidator : AbstractValidator<CommandResultRequest>
{
    public CommandResultRequestValidator()
    {
        RuleFor(r => r.Message).MaximumLength(500);
    }
}
