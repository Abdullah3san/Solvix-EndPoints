namespace Solvix.Modules.Assistant.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>A chat thread. Private to the user who started it, even from tenant admins.</summary>
public sealed class AssistantConversation : TenantEntity
{
    public const int TitleMaxLength = 200;

    private AssistantConversation()
    {
    }

    private AssistantConversation(Guid tenantId, Guid userId, string title, DateTimeOffset now)
        : base(tenantId)
    {
        UserId = userId;
        Title = title;
        LastMessageAtUtc = now;
    }

    public Guid UserId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public DateTimeOffset LastMessageAtUtc { get; private set; }

    public bool IsArchived { get; private set; }

    public static AssistantConversation Start(Guid tenantId, Guid userId, string firstMessage, DateTimeOffset now)
    {
        var title = firstMessage.Length <= 80 ? firstMessage : firstMessage[..80].TrimEnd() + "…";
        return new AssistantConversation(tenantId, userId, Guard.NotNullOrWhiteSpace(title, "title", TitleMaxLength), now);
    }

    public void Touch(DateTimeOffset now) => LastMessageAtUtc = now;

    public void Archive() => IsArchived = true;
}
