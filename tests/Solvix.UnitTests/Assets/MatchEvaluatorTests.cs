namespace Solvix.UnitTests.Assets;

using Solvix.Modules.Assets.Domain;

public sealed class MatchEvaluatorTests
{
    private static readonly HardwareFacts DellAsset = new("5CD1234", "Dell Inc.", "Latitude 7440");

    [Fact]
    public void Serial_manufacturer_and_model_match_is_high_and_unambiguous()
    {
        var result = MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("5cd1234", "DELL", "Latitude 7440"));
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.True(result.SerialMatches);
        Assert.True(result.IsUnambiguousHigh);
        Assert.Equal(100, result.Score);
    }

    [Fact]
    public void Partial_or_unknown_model_keeps_high_confidence()
    {
        Assert.Equal(MatchConfidence.High, MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("5CD1234", "Dell", "Dell Latitude 7440 Notebook")).Confidence);
        Assert.Equal(MatchConfidence.High, MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("5CD1234", "Dell", null)).Confidence);
    }

    [Fact]
    public void Model_mismatch_or_unknown_manufacturer_is_medium()
    {
        var model = MatchEvaluator.Evaluate(new("PF12345", "Lenovo", "ThinkPad X1 Carbon"), AssetCategory.Laptop, new("PF12345", "LENOVO", "20XW0026US"));
        Assert.Equal(MatchConfidence.Medium, model.Confidence);
        Assert.Contains(model.Evidence, e => e.Kind == MatchEvaluator.Model && e.Outcome == EvidenceOutcome.Mismatch);

        Assert.Equal(MatchConfidence.Medium, MatchEvaluator.Evaluate(DellAsset with { Manufacturer = null }, AssetCategory.Laptop, new("5CD1234", "Dell", "Latitude 7440")).Confidence);
    }

    [Fact]
    public void Manufacturer_mismatch_is_low()
    {
        var result = MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("5CD1234", "HP", "EliteBook"));
        Assert.Equal(MatchConfidence.Low, result.Confidence);
        Assert.Contains(result.Evidence, e => e.Kind == MatchEvaluator.Manufacturer && e.Outcome == EvidenceOutcome.Mismatch);
    }

    [Fact]
    public void No_serial_match_is_never_a_candidate()
    {
        var different = MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("OTHER", "Dell", "Latitude 7440"));
        Assert.False(different.SerialMatches);
        Assert.Equal(MatchConfidence.Low, different.Confidence);

        var placeholder = MatchEvaluator.Evaluate(DellAsset with { SerialNumber = "To be filled by O.E.M." }, AssetCategory.Laptop, new("To be filled by O.E.M.", "Dell", "Latitude 7440"));
        Assert.False(placeholder.SerialMatches);
        Assert.Contains(placeholder.Evidence, e => e.Kind == MatchEvaluator.SerialNumber && e.Outcome == EvidenceOutcome.Unknown);
    }

    [Fact]
    public void Categories_without_an_agent_are_never_high()
    {
        var result = MatchEvaluator.Evaluate(DellAsset, AssetCategory.Monitor, new("5CD1234", "Dell", "Latitude 7440"));
        Assert.Equal(MatchConfidence.Medium, result.Confidence);
        Assert.Contains(result.Evidence, e => e.Kind == MatchEvaluator.Category && e.Outcome == EvidenceOutcome.Warning);
    }

    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(1, 2, false)]
    [InlineData(1, 1, true)]
    public void Multiple_candidates_or_existing_links_are_ambiguous(int assets, int devices, bool linkedElsewhere)
    {
        var result = MatchEvaluator.Evaluate(DellAsset, AssetCategory.Laptop, new("5CD1234", "Dell", "Latitude 7440"), assets, devices, linkedElsewhere);
        Assert.Equal(MatchConfidence.High, result.Confidence);
        Assert.True(result.IsAmbiguous);
        Assert.False(result.IsUnambiguousHigh);
        Assert.Contains(result.Evidence, e => e.Kind == MatchEvaluator.Ambiguity);
    }
}
