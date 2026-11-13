namespace Solvix.UnitTests.Software;

using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Domain;

/// <summary>Rules shared by server, agent and executor: arguments, detection, versions, parameters, restriction rules.</summary>
public sealed class SoftwareLogicTests
{
    [Theory]
    [InlineData("INSTALLDIR=C:\\Program Files\\Contoso")]
    [InlineData("ALLUSERS=1")]
    [InlineData("SERVER_URL=https://contoso.example/api")]
    [InlineData("LICENSEKEY=")]
    public void Public_msi_properties_are_allowed(string argument) =>
        Assert.Null(InstallerArguments.Validate(InstallerType.Msi, [argument]));

    [Theory]
    [InlineData("/i")]
    [InlineData("/qb")]
    [InlineData("installdir=C:\\x")]
    [InlineData("REBOOT=Force")]
    [InlineData("TRANSFORMS=evil.mst")]
    [InlineData("PATCH=x.msp")]
    [InlineData("INSTALLDIR=\"C:\\x\"")]
    [InlineData("ACTION=ADMIN")]
    public void Msi_switches_reserved_properties_and_quotes_are_refused(string argument) =>
        Assert.NotNull(InstallerArguments.Validate(InstallerType.Msi, [argument]));

    [Fact]
    public void An_msi_property_cannot_be_set_twice() =>
        Assert.NotNull(InstallerArguments.Validate(InstallerType.Msi, ["ALLUSERS=1", "ALLUSERS=2"]));

    [Theory]
    [InlineData("/S & calc.exe")]
    [InlineData("/S | net user x /add")]
    [InlineData("/D=%TEMP%")]
    [InlineData("--dir=`whoami`")]
    [InlineData("/LOG>C:\\x")]
    [InlineData(" /S")]
    [InlineData("/S\n/Q")]
    public void Exe_arguments_with_shell_metacharacters_or_control_characters_are_refused(string argument) =>
        Assert.NotNull(InstallerArguments.Validate(InstallerType.Exe, [argument]));

    [Fact]
    public void Exe_arguments_and_presets_are_accepted()
    {
        Assert.Null(InstallerArguments.Validate(InstallerType.Exe, ["/VERYSILENT", "/DIR=C:\\Program Files\\Contoso", "--install-path=D:/apps"]));
        foreach (var preset in Enum.GetValues<SilentPreset>())
        {
            var (install, uninstall) = SilentPresets.Defaults(preset);
            Assert.Null(InstallerArguments.Validate(InstallerType.Exe, install));
            Assert.Null(InstallerArguments.Validate(InstallerType.Exe, uninstall));
        }

        Assert.NotNull(InstallerArguments.Validate(InstallerType.Exe, Enumerable.Repeat("/S", 33).ToList()));
    }

    [Theory]
    [InlineData("1.2.10", "1.2.9", 1)]
    [InlineData("1.2", "1.2.0.0", 0)]
    [InlineData("10.0.19045.3803", "10.0.19045.4046", -1)]
    [InlineData("24.1-beta", "24.1-alpha", 1)]
    [InlineData("2.0", "10.0", -1)]
    public void Versions_compare_numerically_segment_by_segment(string left, string right, int expected) =>
        Assert.Equal(expected, SoftwareVersion.Compare(left, right));

    [Fact]
    public void Detection_by_product_code_and_by_name_honours_publisher_and_version()
    {
        var apps = new[]
        {
            new DetectedApplication("Contoso Notes", "5.1.0", "Contoso Ltd", "{0A1B2C3D-1111-2222-3333-444455556666}"),
            new DetectedApplication("Contoso Notes Helper", "1.0", "Contoso Ltd", null),
            new DetectedApplication("Fabrikam Viewer", "9.0", "Fabrikam", null),
        };

        var byCode = new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = "0a1b2c3d-1111-2222-3333-444455556666", Version = "5.0", VersionOperator = VersionOperator.AtLeast };
        Assert.Equal(new DetectionResult(true, true, "5.1.0"), DetectionEvaluator.Evaluate(byCode, apps));
        Assert.Equal(new DetectionResult(true, false, "5.1.0"), DetectionEvaluator.Evaluate(byCode with { Version = "5.2" }, apps));

        var byName = new DetectionRule { Kind = DetectionKind.UninstallEntry, DisplayName = "contoso notes", Publisher = "Contoso Ltd" };
        Assert.True(DetectionEvaluator.Evaluate(byName, apps).Satisfied);
        Assert.False(DetectionEvaluator.Evaluate(byName with { Publisher = "Fabrikam" }, apps).Installed);
        Assert.Equal("5.1.0", DetectionEvaluator.Evaluate(byName with { NameMatch = NameMatch.Prefix }, apps).InstalledVersion);
        Assert.True(DetectionEvaluator.Evaluate(byName with { DisplayName = "Viewer", NameMatch = NameMatch.Contains, Publisher = null, Version = "9", VersionOperator = VersionOperator.Equal }, apps).Satisfied);
        Assert.False(DetectionEvaluator.Evaluate(byName with { DisplayName = "Notes", NameMatch = NameMatch.Exact }, apps).Installed);
    }

    [Fact]
    public void Detection_rules_are_validated()
    {
        Assert.NotNull(new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = "not-a-guid" }.Validate());
        Assert.NotNull(new DetectionRule { Kind = DetectionKind.UninstallEntry }.Validate());
        Assert.NotNull(new DetectionRule { Kind = DetectionKind.UninstallEntry, DisplayName = "X", VersionOperator = VersionOperator.AtLeast }.Validate());
        Assert.NotNull(new DetectionRule { Kind = DetectionKind.UninstallEntry, DisplayName = "X", Version = "latest", VersionOperator = VersionOperator.AtLeast }.Validate());
        Assert.Null(new DetectionRule { Kind = DetectionKind.UninstallEntry, DisplayName = "X", Version = "1.0", VersionOperator = VersionOperator.AtLeast }.Validate());
    }

    [Fact]
    public void Operation_parameters_round_trip_and_unknown_or_inconsistent_ones_are_refused()
    {
        var operation = Install();
        var parsed = SoftwareOperation.Parse(CommandTypes.SoftwareInstall, operation.ToParameters());
        Assert.Equal(operation.Arguments, parsed.Arguments);
        Assert.Equal(operation.Detection, parsed.Detection);
        Assert.Equal(operation.Sha256, parsed.Sha256);
        Assert.Equal(operation.ExpectedSignerThumbprint, parsed.ExpectedSignerThumbprint);

        var extra = new Dictionary<string, string>(operation.ToParameters()) { ["commandLine"] = "cmd /c whoami" };
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareInstall, extra));

        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareInstall, (operation with { Sha256 = "ABC" }).ToParameters()));
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareInstall, (operation with { RequiresPackage = false, Sha256 = null, SizeBytes = 0 }).ToParameters()));
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareInstall, (operation with { TimeoutSeconds = 10 }).ToParameters()));
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareUninstall, operation.ToParameters())); // MSI removal needs no package or arguments
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.Restart, operation.ToParameters()));
        Assert.Throws<FormatException>(() => SoftwareOperation.Parse(CommandTypes.SoftwareInstall, (operation with { RequireSignature = true, ExpectedSignerThumbprint = null }).ToParameters()));
    }

    [Theory]
    [InlineData("Contoso*", "Contoso Notes", true)]
    [InlineData("*torrent*", "uTorrent", true)]
    [InlineData("Fabrikam ?iewer", "Fabrikam Viewer", true)]
    [InlineData("Fabrikam", "Fabrikam Viewer", false)]
    [InlineData("a.b", "aXb", false)]
    [InlineData("(*)", "(x)", true)]
    public void Restriction_patterns_are_wildcards_not_regular_expressions(string pattern, string name, bool matches) =>
        Assert.Equal(matches, SoftwareRestrictionRule.WildcardMatch(pattern, name));

    [Fact]
    public void Blocked_rules_with_a_minimum_version_only_block_older_versions()
    {
        var rule = new SoftwareRestrictionRule(Guid.NewGuid());
        rule.Update("Old Java", null, RestrictionRuleType.Blocked, "Java*", "Oracle Corporation", "8.0.401", null, true);
        Assert.True(rule.MatchesIdentity("Java 8 Update 381", "oracle corporation"));
        Assert.False(rule.MatchesIdentity("Java 8 Update 381", "Eclipse Adoptium"));
        Assert.True(rule.BlocksVersion("8.0.381"));
        Assert.False(rule.BlocksVersion("8.0.411"));

        rule.Update("No torrents", null, RestrictionRuleType.Blocked, "*torrent*", null, null, [Guid.NewGuid()], true);
        Assert.True(rule.BlocksVersion("1.0"));
        Assert.False(rule.AppliesTo([Guid.NewGuid()]));
    }

    [Fact]
    public void Publisher_thumbprints_are_normalised_and_only_sha256_is_accepted()
    {
        var sha256 = string.Concat(Enumerable.Repeat("ab", 32));
        Assert.Equal(sha256.ToUpperInvariant(), SoftwareDeploymentPolicy.NormalizeThumbprint(string.Join(':', Enumerable.Repeat("ab", 32))));
        Assert.Null(SoftwareDeploymentPolicy.NormalizeThumbprint(new string('a', 40)));

        var policy = SoftwareDeploymentPolicy.CreateDefault(Guid.NewGuid());
        policy.Update(true, false, [sha256], true, 50, true, true, 72);
        Assert.True(policy.IsTrustedPublisher(sha256.ToUpperInvariant()));
        Assert.False(policy.IsTrustedPublisher(null));
    }

    [Fact]
    public void Report_csv_neutralises_spreadsheet_formulas()
    {
        var row = new DeploymentReportRow(Guid.NewGuid(), "=HYPERLINK(\"http://x\")", TargetStatus.Failed, null, 1, 1603, "installer_failed", "+cmd", null, false, null, null, null);
        var report = new DeploymentReport(Guid.NewGuid(), "x", DeploymentAction.Install, DeploymentStatus.Failed, "p", null, new string('a', 64), Guid.NewGuid(),
            DateTimeOffset.UtcNow, [], null, null, new TargetCountsDto(1, 0, 0, 0, 1, 0, 0, 0, 0), 0, 0, null, [], [row], DateTimeOffset.UtcNow, true);
        var csv = DeploymentService.ToCsv(report);
        Assert.Contains("\"'=HYPERLINK(\"\"http://x\"\")\"", csv, StringComparison.Ordinal);
        Assert.Contains("\"'+cmd\"", csv, StringComparison.Ordinal);
    }

    private static SoftwareOperation Install() => new()
    {
        DeploymentId = Guid.CreateVersion7(),
        TargetId = Guid.CreateVersion7(),
        PackageId = Guid.CreateVersion7(),
        PackageName = "Contoso Notes",
        InstallerType = InstallerType.Msi,
        RequiresPackage = true,
        Sha256 = new string('a', 64),
        SizeBytes = 1000,
        Arguments = ["ALLUSERS=1"],
        Detection = new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = "{0A1B2C3D-1111-2222-3333-444455556666}" },
        ExpectedSignerThumbprint = new string('A', 64),
        RequireSignature = true,
    };
}
