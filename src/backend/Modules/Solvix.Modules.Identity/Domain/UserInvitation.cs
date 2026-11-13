namespace Solvix.Modules.Identity.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Guards;

/// <summary>
/// An invitation to join a tenant with a fixed set of roles. The link in the
/// e-mail carries a random token; only its hash is stored. The roles are
/// decided by the inviter (and checked against what the inviter may grant) and
/// cannot be influenced by whoever accepts the invitation.
/// </summary>
public sealed class UserInvitation : TenantEntity
{
    public const int TokenHashLength = 64;

    private readonly List<UserInvitationRole> _roles = [];

    private UserInvitation()
    {
    }

    private UserInvitation(Guid tenantId, string email, string? displayName, string tokenHash, DateTimeOffset expiresAtUtc, Guid invitedByUserId)
        : base(tenantId)
    {
        Email = email;
        NormalizedEmail = User.NormalizeEmail(email);
        DisplayName = displayName;
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
        InvitedByUserId = invitedByUserId;
    }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string? DisplayName { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAtUtc { get; private set; }

    public Guid InvitedByUserId { get; private set; }

    public DateTimeOffset? AcceptedAtUtc { get; private set; }

    public Guid? AcceptedUserId { get; private set; }

    public DateTimeOffset? RevokedAtUtc { get; private set; }

    public IReadOnlyCollection<UserInvitationRole> Roles => _roles;

    public bool IsPending => AcceptedAtUtc is null && RevokedAtUtc is null;

    public static UserInvitation Create(
        Guid tenantId,
        string email,
        string? displayName,
        IReadOnlyCollection<Guid> roleIds,
        string tokenHash,
        DateTimeOffset expiresAtUtc,
        Guid invitedByUserId)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var invitation = new UserInvitation(
            tenantId,
            Guard.NotNullOrWhiteSpace(email, "email", User.EmailMaxLength),
            Guard.Optional(displayName, "displayName", User.DisplayNameMaxLength),
            tokenHash,
            expiresAtUtc,
            invitedByUserId);
        foreach (var roleId in roleIds.Distinct())
        {
            invitation._roles.Add(UserInvitationRole.Create(tenantId, invitation.Id, roleId));
        }

        return invitation;
    }

    public InvitationStatus Status(DateTimeOffset now) =>
        AcceptedAtUtc is not null ? InvitationStatus.Accepted
        : RevokedAtUtc is not null ? InvitationStatus.Revoked
        : now >= ExpiresAtUtc ? InvitationStatus.Expired
        : InvitationStatus.Pending;

    public bool IsUsable(DateTimeOffset now) => Status(now) == InvitationStatus.Pending;

    /// <summary>Makes the roles to be granted exactly <paramref name="roleIds"/>. Requires <see cref="Roles"/> to be loaded.</summary>
    public void SetRoles(IReadOnlyCollection<Guid> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        _roles.RemoveAll(r => !roleIds.Contains(r.RoleId));
        foreach (var roleId in roleIds.Distinct())
        {
            if (_roles.TrueForAll(r => r.RoleId != roleId))
            {
                _roles.Add(UserInvitationRole.Create(TenantId, Id, roleId));
            }
        }
    }

    public void Rename(string? displayName) =>
        DisplayName = Guard.Optional(displayName, "displayName", User.DisplayNameMaxLength);

    /// <summary>Replaces the link: the old token stops working and the expiry restarts.</summary>
    public void Reissue(string tokenHash, DateTimeOffset expiresAtUtc)
    {
        TokenHash = tokenHash;
        ExpiresAtUtc = expiresAtUtc;
    }

    public void Accept(Guid userId, DateTimeOffset at)
    {
        AcceptedAtUtc = at;
        AcceptedUserId = userId;
    }

    public void Revoke(DateTimeOffset at) => RevokedAtUtc ??= at;
}

public enum InvitationStatus
{
    Pending = 1,
    Accepted = 2,
    Revoked = 3,
    Expired = 4,
}

public sealed class UserInvitationRole : TenantEntity
{
    private UserInvitationRole()
    {
    }

    private UserInvitationRole(Guid tenantId, Guid invitationId, Guid roleId)
        : base(tenantId)
    {
        InvitationId = invitationId;
        RoleId = roleId;
    }

    public Guid InvitationId { get; private set; }

    public Guid RoleId { get; private set; }

    public Role? Role { get; private set; }

    public static UserInvitationRole Create(Guid tenantId, Guid invitationId, Guid roleId) => new(tenantId, invitationId, roleId);
}
