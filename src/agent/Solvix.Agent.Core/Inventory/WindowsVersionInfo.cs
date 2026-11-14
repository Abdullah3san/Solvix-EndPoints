namespace Solvix.Agent.Core.Inventory;

/// <summary>
/// Windows version information module: maps kernel build numbers and product
/// type to product families and support status. Supported: Windows 10,
/// Windows 11, Windows Server 2016/2019/2022/2025.
/// </summary>
public static class WindowsVersionInfo
{
    /// <summary>Win32_OperatingSystem.ProductType values.</summary>
    public enum ProductType
    {
        Workstation = 1,
        DomainController = 2,
        Server = 3,
    }

    public const int Windows10MinimumBuild = 10240;
    public const int Windows11MinimumBuild = 22000;

    private static readonly (int Build, string Name)[] ServerReleases =
    [
        (26100, "Windows Server 2025"),
        (20348, "Windows Server 2022"),
        (17763, "Windows Server 2019"),
        (14393, "Windows Server 2016"),
    ];

    private static readonly Dictionary<int, string> ClientDisplayVersions = new()
    {
        [10240] = "1507", [10586] = "1511", [14393] = "1607", [15063] = "1703", [16299] = "1709",
        [17134] = "1803", [17763] = "1809", [18362] = "1903", [18363] = "1909", [19041] = "2004",
        [19042] = "20H2", [19043] = "21H1", [19044] = "21H2", [19045] = "22H2",
        [22000] = "21H2", [22621] = "22H2", [22631] = "23H2", [26100] = "24H2", [26200] = "25H2",
    };

    public static string Family(int build, ProductType productType)
    {
        if (productType is ProductType.Server or ProductType.DomainController)
        {
            foreach (var (minimum, name) in ServerReleases)
            {
                if (build >= minimum)
                {
                    return name;
                }
            }

            return "Windows Server (unsupported)";
        }

        return build >= Windows11MinimumBuild ? "Windows 11"
            : build >= Windows10MinimumBuild ? "Windows 10"
            : "Windows (unsupported)";
    }

    /// <summary>Whether the platform is a supported target of this agent.</summary>
    public static bool IsSupported(int build, ProductType productType) =>
        productType is ProductType.Server or ProductType.DomainController
            ? build >= 14393
            : build >= Windows10MinimumBuild;

    /// <summary>Marketing version for a build when the registry value is missing (older releases).</summary>
    public static string? DisplayVersionFor(int build, ProductType productType) =>
        productType == ProductType.Workstation && ClientDisplayVersions.TryGetValue(build, out var v) ? v : null;

    /// <summary>
    /// Builds the friendly name. Windows 11 still reports "Windows 10 ..." in
    /// ProductName, so the family is derived from the build number.
    /// </summary>
    public static string FriendlyName(int build, ProductType productType, string? productName, string? editionId)
    {
        var family = Family(build, productType);
        if (productType == ProductType.Workstation)
        {
            var edition = EditionFromProductName(productName) ?? editionId switch
            {
                "Professional" => "Pro",
                "Enterprise" => "Enterprise",
                "Education" => "Education",
                "Core" => "Home",
                "EnterpriseS" => "Enterprise LTSC",
                "IoTEnterprise" => "IoT Enterprise",
                _ => editionId,
            };
            return string.IsNullOrWhiteSpace(edition) ? family : $"{family} {edition}";
        }

        var serverEdition = productName is null ? null : EditionFromProductName(productName);
        return string.IsNullOrWhiteSpace(serverEdition) ? family : $"{family} {serverEdition}";
    }

    private static string? EditionFromProductName(string? productName)
    {
        if (string.IsNullOrWhiteSpace(productName))
        {
            return null;
        }

        // "Windows 10 Pro" -> "Pro", "Windows Server 2022 Datacenter" -> "Datacenter"
        var tokens = productName.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var index = tokens.FindIndex(t => t.All(char.IsAsciiDigit) || t is "10" or "11");
        return index >= 0 && index < tokens.Count - 1 ? string.Join(' ', tokens.Skip(index + 1)) : null;
    }
}
