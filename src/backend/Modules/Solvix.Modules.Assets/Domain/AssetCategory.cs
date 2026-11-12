namespace Solvix.Modules.Assets.Domain;

public enum AssetCategory
{
    Desktop = 1,
    Laptop = 2,
    Server = 3,
    Printer = 4,
    Monitor = 5,
    MobilePhone = 6,
    Tablet = 7,
    ZebraHandheld = 8,
    NetworkSwitch = 9,
    Router = 10,
    Firewall = 11,
    AccessPoint = 12,
    StorageDevice = 13,
    Other = 14,
}

/// <summary>Static metadata of a category.</summary>
/// <param name="Code">Three-letter code used in automatically generated asset tags (AST-LAP-000042).</param>
/// <param name="EndpointManageable">Whether assets of this category can run the Solvix Windows agent and therefore be matched to a managed endpoint.</param>
/// <param name="DefaultUsefulLifeMonths">Default depreciation period suggested when an asset is registered.</param>
public sealed record AssetCategoryInfo(AssetCategory Category, string Code, string DisplayName, bool EndpointManageable, int DefaultUsefulLifeMonths);

public static class AssetCategories
{
    public static IReadOnlyList<AssetCategoryInfo> All { get; } =
    [
        new(AssetCategory.Desktop, "DSK", "Desktop", true, 48),
        new(AssetCategory.Laptop, "LAP", "Laptop", true, 36),
        new(AssetCategory.Server, "SRV", "Server", true, 60),
        new(AssetCategory.Printer, "PRN", "Printer", false, 60),
        new(AssetCategory.Monitor, "MON", "Monitor", false, 60),
        new(AssetCategory.MobilePhone, "MOB", "Mobile phone", false, 24),
        new(AssetCategory.Tablet, "TAB", "Tablet", true, 36),
        new(AssetCategory.ZebraHandheld, "HHD", "Zebra handheld", false, 48),
        new(AssetCategory.NetworkSwitch, "SWT", "Network switch", false, 84),
        new(AssetCategory.Router, "RTR", "Router", false, 84),
        new(AssetCategory.Firewall, "FWL", "Firewall", false, 60),
        new(AssetCategory.AccessPoint, "WAP", "Access point", false, 60),
        new(AssetCategory.StorageDevice, "STO", "Storage device", false, 60),
        new(AssetCategory.Other, "OTH", "Other IT equipment", false, 48),
    ];

    private static readonly Dictionary<AssetCategory, AssetCategoryInfo> ByCategory = All.ToDictionary(c => c.Category);

    public static AssetCategoryInfo Get(AssetCategory category) =>
        ByCategory.TryGetValue(category, out var info) ? info : throw new ArgumentOutOfRangeException(nameof(category), category, null);
}
