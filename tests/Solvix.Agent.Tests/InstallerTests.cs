namespace Solvix.Agent.Tests;

using System.Text.Json;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Enrollment;
using Solvix.Agent.Core.Installation;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Security;

/// <summary>
/// The double-click installer. The Windows commands themselves only run on Windows; here the exact
/// command sequence, the pre-flight checks and the failure handling are verified with a recording runner.
/// </summary>
public sealed class InstallerTests : IDisposable
{
    private static readonly InstallEnvironment Windows11 = new(22631, WindowsVersionInfo.ProductType.Workstation, true, "Windows 11 Pro");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solvix-install-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingRunner _runner = new();
    private readonly InstallOptions _options;

    public InstallerTests()
    {
        var source = Path.Combine(_root, "download");
        Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "Solvix.Agent.exe"), [1, 2, 3]);
        File.WriteAllText(Path.Combine(source, "appsettings.json"), "{}");
        _options = new InstallOptions
        {
            SourceExePath = Path.Combine(source, "Solvix.Agent.exe"),
            InstallDirectory = Path.Combine(_root, "Program Files", "Solvix", "Agent"),
            DataDirectory = Path.Combine(_root, "ProgramData", "Solvix", "Agent"),
        };
    }

    [Fact]
    public async Task Fresh_install_creates_a_least_privilege_service_and_starts_it_after_enrollment_is_stored()
    {
        var enrolled = false;
        var result = await Installer().InstallAsync(_options, Windows11, () =>
        {
            Assert.Contains(_runner.Calls, c => c.StartsWith("schtasks.exe /Create", StringComparison.Ordinal)); // everything else is in place first
            Assert.DoesNotContain(_runner.Calls, c => c.StartsWith("sc.exe start", StringComparison.Ordinal));
            enrolled = true;
        }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.True(enrolled);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(_options.ExePath));
        Assert.True(File.Exists(Path.Combine(_options.InstallDirectory, "appsettings.json")));

        var exe = _options.ExePath;
        Assert.Contains($"sc.exe create SolvixAgent binPath= \"\\\"{exe}\\\"\" DisplayName= \"Solvix Agent\" start= delayed-auto obj= \"NT SERVICE\\SolvixAgent\"", _runner.Calls);
        Assert.Contains("sc.exe sidtype SolvixAgent unrestricted", _runner.Calls);
        Assert.Contains("sc.exe privs SolvixAgent SeChangeNotifyPrivilege/SeCreateGlobalPrivilege", _runner.Calls);
        Assert.Contains(_runner.Calls, c => c.StartsWith("sc.exe failure SolvixAgent reset= 86400 actions= restart/", StringComparison.Ordinal));
        Assert.Equal("sc.exe start SolvixAgent", _runner.Calls[^1]);

        // Data directory: SYSTEM + Administrators, then the service account; never Users/Everyone.
        var dataAcls = _runner.Calls.Where(c => c.StartsWith("icacls.exe", StringComparison.Ordinal) && c.Contains(_options.DataDirectory, StringComparison.Ordinal)).ToList();
        Assert.Contains(dataAcls, c => c.Contains("/inheritance:r", StringComparison.Ordinal) && c.Contains("*S-1-5-18", StringComparison.Ordinal) && c.Contains("*S-1-5-32-544", StringComparison.Ordinal) && !c.Contains("*S-1-5-32-545", StringComparison.Ordinal));
        Assert.Contains(dataAcls, c => c.Contains("NT SERVICE\\SolvixAgent:(OI)(CI)M", StringComparison.Ordinal));

        // The binary the SYSTEM updater runs cannot be modified by non-administrators.
        Assert.Contains(_runner.Calls, c => c.StartsWith("icacls.exe", StringComparison.Ordinal) && c.Contains(_options.InstallDirectory, StringComparison.Ordinal) && c.Contains("*S-1-5-32-545:(OI)(CI)RX", StringComparison.Ordinal));

        // Updater tasks run the installed exe as SYSTEM with the fixed apply-update command.
        var tasks = _runner.Calls.Where(c => c.StartsWith("schtasks.exe /Create", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, t =>
        {
            Assert.Contains("/RU SYSTEM", t, StringComparison.Ordinal);
            Assert.Contains($"\\\"{exe}\\\" apply-update", t, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Reinstall_stops_the_running_service_replaces_the_binary_and_reconfigures_instead_of_creating()
    {
        Directory.CreateDirectory(_options.InstallDirectory);
        await File.WriteAllBytesAsync(_options.ExePath, [9, 9]);
        _runner.ExistingService = true;

        var result = await Installer().InstallAsync(_options, Windows11, () => { }, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(_options.ExePath));
        Assert.Contains("sc.exe stop SolvixAgent", _runner.Calls);
        Assert.DoesNotContain(_runner.Calls, c => c.StartsWith("sc.exe create", StringComparison.Ordinal));
        Assert.Contains(_runner.Calls, c => c.StartsWith("sc.exe config SolvixAgent binPath=", StringComparison.Ordinal));
        Assert.True(_runner.Calls.IndexOf("sc.exe stop SolvixAgent") < _runner.Calls.FindIndex(c => c.StartsWith("sc.exe config", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Running_the_installed_copy_does_not_copy_onto_itself()
    {
        Directory.CreateDirectory(_options.InstallDirectory);
        await File.WriteAllBytesAsync(_options.ExePath, [7]);
        var result = await Installer().InstallAsync(_options with { SourceExePath = _options.ExePath }, Windows11, () => { }, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Equal([7], await File.ReadAllBytesAsync(_options.ExePath));
    }

    [Theory]
    [InlineData(9600, WindowsVersionInfo.ProductType.Workstation, true)]
    [InlineData(9600, WindowsVersionInfo.ProductType.Server, true)]
    [InlineData(22631, WindowsVersionInfo.ProductType.Workstation, false)]
    public async Task Unsupported_systems_are_refused_before_anything_changes(int build, WindowsVersionInfo.ProductType type, bool is64)
    {
        var result = await Installer().InstallAsync(_options, new InstallEnvironment(build, type, is64, "Test OS"), () => Assert.Fail("must not enroll"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(_runner.Calls);
        Assert.False(Directory.Exists(_options.InstallDirectory));
    }

    [Theory]
    [InlineData(19045, WindowsVersionInfo.ProductType.Workstation)]
    [InlineData(22631, WindowsVersionInfo.ProductType.Workstation)]
    [InlineData(14393, WindowsVersionInfo.ProductType.Server)]
    [InlineData(20348, WindowsVersionInfo.ProductType.Server)]
    [InlineData(26100, WindowsVersionInfo.ProductType.DomainController)]
    public void Supported_systems_pass_the_preflight_check(int build, WindowsVersionInfo.ProductType type) =>
        Assert.Null(AgentInstaller.CheckEnvironment(new InstallEnvironment(build, type, true, "ok")));

    [Fact]
    public async Task A_failing_step_stops_the_installation_and_never_stores_or_starts_anything()
    {
        _runner.FailOn = "sc.exe create";
        var result = await Installer().InstallAsync(_options, Windows11, () => Assert.Fail("must not enroll after a failure"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("creating the service", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, c => c.StartsWith("sc.exe start", StringComparison.Ordinal));
        Assert.DoesNotContain(_runner.Calls, c => c.StartsWith("schtasks.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_service_that_will_not_stop_aborts_a_reinstall()
    {
        _runner.ExistingService = true;
        _runner.NeverStops = true;
        var result = await Installer().InstallAsync(_options, Windows11, () => Assert.Fail("must not enroll"), CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("did not stop", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_service_that_installs_but_cannot_start_is_reported()
    {
        _runner.FailOn = "sc.exe start";
        var result = await Installer().InstallAsync(_options, Windows11, () => { }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("could not be started", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uninstall_removes_the_service_and_both_updater_tasks()
    {
        _runner.ExistingService = true;
        var result = await Installer().UninstallAsync(_options, CancellationToken.None);
        Assert.True(result.Success, result.Message);
        Assert.Contains("sc.exe stop SolvixAgent", _runner.Calls);
        Assert.Contains("sc.exe delete SolvixAgent", _runner.Calls);
        Assert.Equal(2, _runner.Calls.Count(c => c.StartsWith("schtasks.exe /Delete /F", StringComparison.Ordinal)));

        _runner.Calls.Clear();
        _runner.ExistingService = false;
        Assert.True((await Installer().UninstallAsync(_options, CancellationToken.None)).Success); // idempotent
        Assert.DoesNotContain("sc.exe delete SolvixAgent", _runner.Calls);
    }

    [Fact]
    public void Enrollment_file_is_validated()
    {
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        string Write(AgentEnrollmentFile file)
        {
            var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(path, JsonSerializer.Serialize(file, AgentJsonContext.Default.AgentEnrollmentFile));
            return path;
        }

        var good = EnrollmentRequestSettings.TryLoad(
            Write(new AgentEnrollmentFile { ServerUrl = "https://agents.example.com:8443/", EnrollmentToken = "svxe_abc", ServerCertificateSha256 = new string('a', 32) + ":" + new string('b', 32), TokenExpiresAtUtc = now.AddHours(1) }),
            now,
            out var error);
        Assert.Null(error);
        Assert.Equal("https://agents.example.com:8443/", good!.ServerUrl.AbsoluteUri);
        Assert.Equal(64, good.ServerCertificateSha256!.Length); // normalised: no colons, upper case

        Assert.Null(EnrollmentRequestSettings.TryLoad(Write(new AgentEnrollmentFile { ServerUrl = "http://insecure/", EnrollmentToken = "svxe_abc" }), now, out error));
        Assert.Contains("https", error, StringComparison.Ordinal);
        Assert.Null(EnrollmentRequestSettings.TryLoad(Write(new AgentEnrollmentFile { ServerUrl = "https://a/", EnrollmentToken = "not-a-token" }), now, out error));
        Assert.Null(EnrollmentRequestSettings.TryLoad(Write(new AgentEnrollmentFile { ServerUrl = "https://a/", EnrollmentToken = "svxe_abc", ServerCertificateSha256 = "zz" }), now, out error));
        Assert.Null(EnrollmentRequestSettings.TryLoad(Write(new AgentEnrollmentFile { ServerUrl = "https://a/", EnrollmentToken = "svxe_abc", TokenExpiresAtUtc = now.AddMinutes(-1) }), now, out error));
        Assert.Contains("expired", error, StringComparison.Ordinal);
        Assert.Null(EnrollmentRequestSettings.TryLoad(Write(new AgentEnrollmentFile { ServerUrl = "https://a/", EnrollmentToken = "svxe_abc", FormatVersion = 99 }), now, out error));

        var broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.Null(EnrollmentRequestSettings.TryLoad(broken, now, out error));
        Assert.Null(EnrollmentRequestSettings.TryLoad(Path.Combine(_root, "missing.json"), now, out error));
    }

    [Fact]
    public void Enrollment_setup_stores_machine_settings_and_the_token_for_the_agent()
    {
        using var agent = new AgentHarness();
        var settings = EnrollmentRequestSettings.Validate("https://agents.example.com/", "svxe_token", new string('c', 64), out _)!;
        agent.Get<EnrollmentSetup>().Apply(settings);

        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(agent.DataDirectory, "agent.json")));
        var section = json.RootElement.GetProperty("SolvixAgent");
        Assert.Equal("https://agents.example.com/", section.GetProperty("ServerUrl").GetString());
        Assert.Equal(new string('C', 64), section.GetProperty("TrustedServerCertificateSha256")[0].GetString());
        Assert.Equal("svxe_token", agent.Get<BootstrapTokenStore>().Read());
        Assert.DoesNotContain("svxe_token", File.ReadAllText(Path.Combine(agent.DataDirectory, "agent.json")), StringComparison.Ordinal); // the token is never in the plain settings file
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private AgentInstaller Installer() => new(_runner, delay: (_, _) => Task.CompletedTask);

    private sealed class RecordingRunner : ICommandRunner
    {
        public List<string> Calls { get; } = [];

        public bool ExistingService { get; set; }

        public bool NeverStops { get; set; }

        public string? FailOn { get; set; }

        public Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
        {
            var call = $"{fileName} {arguments}";
            Calls.Add(call);
            if (FailOn is not null && call.StartsWith(FailOn, StringComparison.Ordinal))
            {
                return Task.FromResult(new CommandResult(5, "Access is denied."));
            }

            if (call.StartsWith("sc.exe query", StringComparison.Ordinal))
            {
                if (!ExistingService)
                {
                    return Task.FromResult(new CommandResult(1060, "The specified service does not exist as an installed service."));
                }

                var stopped = !NeverStops && Calls.Any(c => c.StartsWith("sc.exe stop", StringComparison.Ordinal));
                return Task.FromResult(new CommandResult(0, stopped ? "STATE : 1 STOPPED" : "STATE : 4 RUNNING"));
            }

            return Task.FromResult(new CommandResult(0, string.Empty));
        }
    }
}
