namespace Solvix.Modules.Assets.Domain;

using Solvix.SharedKernel.Endpoints;

public enum EvidenceOutcome
{
    Match = 1,
    Partial = 2,
    Mismatch = 3,
    Unknown = 4,
    Warning = 5,
}

/// <summary>One piece of evidence shown to the reviewer, e.g. (SerialNumber, Match, "5CD1234XYZ").</summary>
public sealed record MatchEvidence(string Kind, EvidenceOutcome Outcome, string Detail);

public sealed record MatchResult(MatchConfidence Confidence, int Score, bool SerialMatches, bool IsAmbiguous, IReadOnlyList<MatchEvidence> Evidence)
{
    /// <summary>Eligible for automatic approval: High confidence and nothing ambiguous on either side.</summary>
    public bool IsUnambiguousHigh => Confidence == MatchConfidence.High && !IsAmbiguous;
}

/// <summary>Hardware identity of one side of a potential match.</summary>
public sealed record HardwareFacts(string? SerialNumber, string? Manufacturer, string? Model);

/// <summary>
/// Scores how strongly an asset and a managed endpoint describe the same
/// hardware. Serial number + manufacturer are the primary evidence; the model
/// supports it (OEMs report machine-type codes such as "20XW0026US" where the
/// asset register says "ThinkPad X1 Carbon", so a model difference alone never
/// rejects a match). A serial number match is required for a suggestion.
/// </summary>
public static class MatchEvaluator
{
    public const string SerialNumber = "serialNumber";
    public const string Manufacturer = "manufacturer";
    public const string Model = "model";
    public const string Category = "category";
    public const string Ambiguity = "ambiguity";

    public static MatchResult Evaluate(HardwareFacts asset, AssetCategory category, HardwareFacts device, int assetCandidatesForDevice = 1, int deviceCandidatesForAsset = 1, bool alreadyLinkedElsewhere = false)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(device);
        var evidence = new List<MatchEvidence>();
        var score = 0;

        var assetSerial = HardwareIdentity.NormalizeSerial(asset.SerialNumber);
        var deviceSerial = HardwareIdentity.NormalizeSerial(device.SerialNumber);
        var serialMatches = assetSerial is not null && assetSerial == deviceSerial;
        if (serialMatches)
        {
            score += 60;
            evidence.Add(new(SerialNumber, EvidenceOutcome.Match, assetSerial!));
        }
        else if (assetSerial is null || deviceSerial is null)
        {
            evidence.Add(new(SerialNumber, EvidenceOutcome.Unknown, assetSerial is null ? "The asset has no usable serial number." : "The endpoint reports no usable serial number."));
        }
        else
        {
            score -= 60;
            evidence.Add(new(SerialNumber, EvidenceOutcome.Mismatch, $"Asset {asset.SerialNumber} ≠ endpoint {device.SerialNumber}"));
        }

        var assetMaker = HardwareIdentity.NormalizeManufacturer(asset.Manufacturer);
        var deviceMaker = HardwareIdentity.NormalizeManufacturer(device.Manufacturer);
        var makerOutcome = assetMaker is null || deviceMaker is null ? EvidenceOutcome.Unknown
            : assetMaker == deviceMaker ? EvidenceOutcome.Match
            : EvidenceOutcome.Mismatch;
        score += makerOutcome switch { EvidenceOutcome.Match => 25, EvidenceOutcome.Mismatch => -30, _ => 0 };
        evidence.Add(new(Manufacturer, makerOutcome, Describe(asset.Manufacturer, device.Manufacturer)));

        var assetModel = HardwareIdentity.NormalizeModel(asset.Model);
        var deviceModel = HardwareIdentity.NormalizeModel(device.Model);
        var modelOutcome = assetModel is null || deviceModel is null ? EvidenceOutcome.Unknown
            : assetModel == deviceModel ? EvidenceOutcome.Match
            : Math.Min(assetModel.Length, deviceModel.Length) >= 4 && (assetModel.Contains(deviceModel, StringComparison.Ordinal) || deviceModel.Contains(assetModel, StringComparison.Ordinal))
                ? EvidenceOutcome.Partial
                : EvidenceOutcome.Mismatch;
        score += modelOutcome switch { EvidenceOutcome.Match => 15, EvidenceOutcome.Partial => 8, EvidenceOutcome.Mismatch => -5, _ => 0 };
        evidence.Add(new(Model, modelOutcome, Describe(asset.Model, device.Model)));

        var manageable = AssetCategories.Get(category).EndpointManageable;
        if (!manageable)
        {
            score -= 20;
            evidence.Add(new(Category, EvidenceOutcome.Warning, $"{AssetCategories.Get(category).DisplayName} assets do not normally run the Windows agent."));
        }

        var ambiguous = assetCandidatesForDevice > 1 || deviceCandidatesForAsset > 1 || alreadyLinkedElsewhere;
        if (assetCandidatesForDevice > 1)
        {
            evidence.Add(new(Ambiguity, EvidenceOutcome.Warning, $"{assetCandidatesForDevice} assets share this serial number."));
        }

        if (deviceCandidatesForAsset > 1)
        {
            evidence.Add(new(Ambiguity, EvidenceOutcome.Warning, $"{deviceCandidatesForAsset} endpoints report this serial number."));
        }

        if (alreadyLinkedElsewhere)
        {
            evidence.Add(new(Ambiguity, EvidenceOutcome.Warning, "The asset or the endpoint is already associated with another record."));
        }

        MatchConfidence confidence;
        if (serialMatches && makerOutcome == EvidenceOutcome.Match && modelOutcome != EvidenceOutcome.Mismatch && manageable)
        {
            confidence = MatchConfidence.High;
        }
        else if (serialMatches && makerOutcome != EvidenceOutcome.Mismatch)
        {
            confidence = MatchConfidence.Medium;
        }
        else
        {
            confidence = MatchConfidence.Low;
        }

        return new MatchResult(confidence, Math.Clamp(score, -100, 100), serialMatches, ambiguous, evidence);
    }

    private static string Describe(string? assetValue, string? deviceValue) =>
        $"Asset: {(string.IsNullOrWhiteSpace(assetValue) ? "—" : assetValue.Trim())} · Endpoint: {(string.IsNullOrWhiteSpace(deviceValue) ? "—" : deviceValue.Trim())}";
}
