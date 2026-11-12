namespace Solvix.Modules.Assets.Configuration;

public sealed class AssetsOptions
{
    public const string SectionName = "Solvix:Assets";

    /// <summary>
    /// Public base URL of the web application (e.g. https://endpoints.example.com). When set,
    /// QR codes encode {base}/assets/{id} so a phone camera opens the asset (after sign-in);
    /// otherwise they encode "SOLVIX-ASSET:{tag}", which the in-app scanner understands.
    /// </summary>
    public string? LabelBaseUrl { get; set; }
}
