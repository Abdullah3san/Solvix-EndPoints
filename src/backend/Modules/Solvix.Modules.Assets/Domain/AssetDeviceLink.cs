namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Entities;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Guards;

/// <summary>
/// Association between a physical <see cref="Asset"/> and a managed endpoint
/// (device). The two records stay separate; this row is the reviewed claim
/// that they describe the same hardware. At most one Approved link per asset
/// and per device (partial unique indexes); one row per pair, so a rejected
/// pair is remembered and never suggested again.
/// </summary>
public sealed class AssetDeviceLink : TenantEntity
{
    public const int NoteMaxLength = 1000;

    private AssetDeviceLink()
    {
    }

    private AssetDeviceLink(Guid tenantId, Guid assetId, Guid deviceId)
        : base(tenantId)
    {
        AssetId = assetId;
        DeviceId = deviceId;
    }

    public Guid AssetId { get; private set; }

    public Guid DeviceId { get; private set; }

    public AssetDeviceLinkStatus Status { get; private set; }

    public AssetDeviceLinkSource Source { get; private set; }

    public MatchConfidence Confidence { get; private set; }

    public int Score { get; private set; }

    /// <summary>More than one candidate on either side when last evaluated: never approved automatically.</summary>
    public bool IsAmbiguous { get; private set; }

    /// <summary><see cref="MatchEvidence"/> list as JSON (jsonb).</summary>
    public string EvidenceJson { get; private set; } = "[]";

    public DateTimeOffset DetectedAtUtc { get; private set; }

    public DateTimeOffset LastEvaluatedAtUtc { get; private set; }

    public DateTimeOffset? DecidedAtUtc { get; private set; }

    /// <summary>Null for an automatic approval.</summary>
    public Guid? DecidedByUserId { get; private set; }

    public string? DecisionNote { get; private set; }

    public static AssetDeviceLink Suggest(Guid tenantId, Guid assetId, Guid deviceId, MatchResult match, string evidenceJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(match);
        var link = new AssetDeviceLink(tenantId, assetId, deviceId)
        {
            Status = AssetDeviceLinkStatus.Suggested,
            Source = AssetDeviceLinkSource.Automatic,
            DetectedAtUtc = now,
        };
        link.Reevaluate(match, evidenceJson, now);
        return link;
    }

    public static AssetDeviceLink Manual(Guid tenantId, Guid assetId, Guid deviceId, MatchResult match, string evidenceJson, DateTimeOffset now, Guid? actor, string? note)
    {
        ArgumentNullException.ThrowIfNull(match);
        var link = new AssetDeviceLink(tenantId, assetId, deviceId)
        {
            Source = AssetDeviceLinkSource.Manual,
            DetectedAtUtc = now,
        };
        link.Reevaluate(match, evidenceJson, now);
        link.Approve(now, actor, note);
        return link;
    }

    public void Reevaluate(MatchResult match, string evidenceJson, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(match);
        Confidence = match.Confidence;
        Score = match.Score;
        IsAmbiguous = match.IsAmbiguous;
        EvidenceJson = evidenceJson;
        LastEvaluatedAtUtc = now;
    }

    public void Approve(DateTimeOffset now, Guid? actor, string? note)
    {
        if (Status == AssetDeviceLinkStatus.Approved)
        {
            throw new ConflictException("The association is already approved.", "asset_link_status_conflict");
        }

        Status = AssetDeviceLinkStatus.Approved;
        DecidedAtUtc = now;
        DecidedByUserId = actor;
        DecisionNote = Guard.Optional(note, "note", NoteMaxLength);
    }

    /// <summary>Re-opens a remembered pair (rejected, superseded or unlinked) as an approved manual link.</summary>
    public void Relink(DateTimeOffset now, Guid? actor, string? note)
    {
        Source = AssetDeviceLinkSource.Manual;
        Status = AssetDeviceLinkStatus.Suggested;
        Approve(now, actor, note);
    }

    public void Reject(DateTimeOffset now, Guid? actor, string? note)
    {
        if (Status != AssetDeviceLinkStatus.Suggested)
        {
            throw new ConflictException("Only suggested associations can be rejected.", "asset_link_status_conflict");
        }

        Decide(AssetDeviceLinkStatus.Rejected, now, actor, note);
    }

    public void Supersede(DateTimeOffset now, Guid? actor) =>
        Decide(AssetDeviceLinkStatus.Superseded, now, actor, "Another association was approved.");

    public void Unlink(DateTimeOffset now, Guid? actor, string? note)
    {
        if (Status != AssetDeviceLinkStatus.Approved)
        {
            throw new ConflictException("Only approved associations can be removed.", "asset_link_status_conflict");
        }

        Decide(AssetDeviceLinkStatus.Unlinked, now, actor, note);
    }

    private void Decide(AssetDeviceLinkStatus status, DateTimeOffset now, Guid? actor, string? note)
    {
        Status = status;
        DecidedAtUtc = now;
        DecidedByUserId = actor;
        DecisionNote = Guard.Optional(note, "note", NoteMaxLength);
    }
}
