namespace Solvix.Modules.Helpdesk.Application;

using FluentValidation;
using Solvix.Modules.Helpdesk.Domain;

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(Ticket.SubjectMaxLength);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Ticket.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum().When(x => x.Priority is not null);
        RuleFor(x => x.RequesterEmail).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrEmpty(x.RequesterEmail));
        RuleFor(x => x.RequesterName).MaximumLength(200);
    }
}

public sealed class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(Ticket.SubjectMaxLength);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(Ticket.DescriptionMaxLength);
    }
}

public sealed class ChangeStatusRequestValidator : AbstractValidator<ChangeStatusRequest>
{
    public ChangeStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.Resolution).MaximumLength(5000);
    }
}

public sealed class ChangePriorityRequestValidator : AbstractValidator<ChangePriorityRequest>
{
    public ChangePriorityRequestValidator()
    {
        RuleFor(x => x.Priority).IsInEnum();
    }
}

public sealed class AddCommentRequestValidator : AbstractValidator<AddCommentRequest>
{
    public AddCommentRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(TicketComment.BodyMaxLength);
    }
}

public sealed class SaveCategoryRequestValidator : AbstractValidator<SaveCategoryRequest>
{
    public SaveCategoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(TicketCategory.NameMaxLength);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Keywords).Must(k => k is null || k.Count <= 30).WithMessage("At most 30 keywords.");
    }
}

public sealed class CreateTechnicianRequestValidator : AbstractValidator<CreateTechnicianRequest>
{
    public CreateTechnicianRequestValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.MaxOpenTickets).InclusiveBetween(1, 1000).When(x => x.MaxOpenTickets is not null);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class UpdateTechnicianRequestValidator : AbstractValidator<UpdateTechnicianRequest>
{
    public UpdateTechnicianRequestValidator()
    {
        RuleFor(x => x.MaxOpenTickets).InclusiveBetween(1, 1000).When(x => x.MaxOpenTickets is not null);
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class SaveSlaPolicyRequestValidator : AbstractValidator<SaveSlaPolicyRequest>
{
    public SaveSlaPolicyRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Targets).NotNull().Must(t => t is { Count: > 0 and <= 4 }).WithMessage("Provide one target per priority (1-4).");
        RuleForEach(x => x.Targets).ChildRules(t =>
        {
            t.RuleFor(x => x.Priority).IsInEnum();
            t.RuleFor(x => x.FirstResponseMinutes).InclusiveBetween(1, 60 * 24 * 365);
            t.RuleFor(x => x.ResolutionMinutes).InclusiveBetween(1, 60 * 24 * 365);
        });
        RuleFor(x => x.TimeZoneId).MaximumLength(64);
        RuleFor(x => x.WorkdayStartMinute).InclusiveBetween(0, 1439).When(x => x.WorkdayStartMinute is not null);
        RuleFor(x => x.WorkdayEndMinute).InclusiveBetween(1, 1440).When(x => x.WorkdayEndMinute is not null);
        RuleFor(x => x.WorkDaysMask).InclusiveBetween(1, 127).When(x => x.WorkDaysMask is not null);
    }
}

public sealed class SaveEscalationRuleRequestValidator : AbstractValidator<SaveEscalationRuleRequest>
{
    public SaveEscalationRuleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Trigger).IsInEnum();
        RuleFor(x => x.Action).IsInEnum();
        RuleFor(x => x.ThresholdPercent).InclusiveBetween(1, 200);
    }
}

public sealed class SaveInboundChannelRequestValidator : AbstractValidator<SaveInboundChannelRequest>
{
    public SaveInboundChannelRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Address).EmailAddress().MaximumLength(254).When(x => !string.IsNullOrEmpty(x.Address));
        RuleFor(x => x.AllowedSenderDomains).Must(d => d is null || d.Count <= 50).WithMessage("At most 50 domains.");
    }
}

public sealed class InboundEmailRequestValidator : AbstractValidator<InboundEmailRequest>
{
    public InboundEmailRequestValidator()
    {
        RuleFor(x => x.From).NotNull();
        RuleFor(x => x.From!.Email).NotEmpty().MaximumLength(254).When(x => x.From is not null);
        RuleFor(x => x.Subject).MaximumLength(1000);
        RuleFor(x => x.Text).MaximumLength(500_000);
        RuleFor(x => x.Html).MaximumLength(1_000_000);
        RuleFor(x => x.MessageId).MaximumLength(1000);
        RuleFor(x => x.Attachments).Must(a => a is null || a.Count <= 20).WithMessage("Too many attachments.");
    }
}
