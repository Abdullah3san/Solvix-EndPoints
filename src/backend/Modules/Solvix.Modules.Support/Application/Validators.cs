namespace Solvix.Modules.Support.Application;

using FluentValidation;
using Solvix.Agent.Contracts;
using Solvix.Modules.Support.Domain;

public sealed class SubmitTicketRequestValidator : AbstractValidator<SubmitTicketRequest>
{
    public SubmitTicketRequestValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(SubmitTicketRequest.SubjectMaxLength);
        RuleFor(x => x.Description).MaximumLength(SubmitTicketRequest.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum();
        RuleFor(x => x.RequesterName).MaximumLength(Ticket.RequesterMaxLength);
        RuleFor(x => x.RequesterEmail).MaximumLength(Ticket.RequesterMaxLength);
    }
}

public sealed class SendChatMessageRequestValidator : AbstractValidator<SendChatMessageRequest>
{
    public SendChatMessageRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(ChatMessage.BodyMaxLength);
    }
}

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Subject).NotEmpty().MaximumLength(Ticket.SubjectMaxLength);
        RuleFor(x => x.Description).MaximumLength(Ticket.DescriptionMaxLength);
        RuleFor(x => x.Priority).IsInEnum();
    }
}

public sealed class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue);
        RuleFor(x => x.Priority).IsInEnum().When(x => x.Priority.HasValue);
    }
}

public sealed class AddTicketCommentRequestValidator : AbstractValidator<AddTicketCommentRequest>
{
    public AddTicketCommentRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(Ticket.DescriptionMaxLength);
    }
}

public sealed class SendChatRequestValidator : AbstractValidator<SendChatRequest>
{
    public SendChatRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(ChatMessage.BodyMaxLength);
    }
}
