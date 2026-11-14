namespace Solvix.Modules.Assistant;

using FluentValidation;
using Solvix.Modules.Assistant.Chat;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Remediation;
using Solvix.Modules.Assistant.Reports;
using Solvix.Modules.Assistant.Tickets;

public sealed class SendMessageRequestValidator : AbstractValidator<SendMessageRequest>
{
    public SendMessageRequestValidator()
    {
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
    }
}

public sealed class ApproveProposalRequestValidator : AbstractValidator<ApproveProposalRequest>
{
    public ApproveProposalRequestValidator()
    {
        RuleFor(x => x.Confirm).Equal(true).WithMessage("Explicit confirmation is required.");
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class RejectProposalRequestValidator : AbstractValidator<RejectProposalRequest>
{
    public RejectProposalRequestValidator()
    {
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(AssistantTicket.TitleMaxLength);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(AssistantTicket.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.Category).MaximumLength(AssistantTicket.CategoryMaxLength);
        RuleFor(x => x.Devices).Must(d => d is null || d.Count <= 50).WithMessage("At most 50 devices.");
    }
}

public sealed class UpdateTicketStatusRequestValidator : AbstractValidator<UpdateTicketStatusRequest>
{
    public UpdateTicketStatusRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum();
    }
}

public sealed class GenerateReportRequestValidator : AbstractValidator<GenerateReportRequest>
{
    public GenerateReportRequestValidator()
    {
        RuleFor(x => x.Kind).IsInEnum();
        RuleFor(x => x.Title).MaximumLength(AssistantReport.TitleMaxLength);
    }
}

public sealed record KnowledgeSearchRequest(string Query, int? Limit);

public sealed class KnowledgeSearchRequestValidator : AbstractValidator<KnowledgeSearchRequest>
{
    public KnowledgeSearchRequestValidator()
    {
        RuleFor(x => x.Query).NotEmpty().MaximumLength(500);
        RuleFor(x => x.Limit).InclusiveBetween(1, 10).When(x => x.Limit is not null);
    }
}
