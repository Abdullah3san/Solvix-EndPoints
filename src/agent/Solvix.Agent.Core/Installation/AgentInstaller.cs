namespace Solvix.Agent.Core.Installation;

using System.Diagnostics;
using Solvix.Agent.Core.Inventory;

public sealed record CommandResult(int ExitCode, string Output);

/// <summary>Runs an operating system command with a raw argument string (sc.exe needs exact quoting).</summary>
public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

public sealed class ProcessCommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} could not be started.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return new CommandResult(process.ExitCode, (await output + await error).Trim());
    }
}

/// <summary>Facts about the machine the installer checks before changing anything.</summary>
public sealed record InstallEnvironment(int OsBuild, WindowsVersionInfo.ProductType ProductType, bool Is64BitOs, string Caption);

public sealed record InstallOptions
{
    public required string SourceExePath { get; init; }

    public required string InstallDirectory { get; init; }

    public required string DataDirectory { get; init; }

    public string ServiceName { get; init; } = "SolvixAgent";

    /// <summary>Virtual service account: no password, no administrative rights.</summary>
    public string ServiceAccount { get; init; } = @"NT SERVICE\SolvixAgent";

    public string EventSource { get; init; } = "Solvix Agent";

    public string TaskFolder { get; init; } = @"\Solvix\";

    public string ExePath => Path.Combine(InstallDirectory, "Solvix.Agent.exe");
}

public sealed record InstallResult(bool Success, string Message)
{
    public static InstallResult Ok(string message) => new(true, message);

    public static InstallResult Fail(string message) => new(false, message);
}

/// <summary>
/// Installs / removes the agent as a Windows service. This is what <c>Solvix.Agent.exe</c> runs when an
/// administrator double-clicks it next to <c>enrollment.json</c>; it replaces a pasted install script.
///
/// Least privilege: the service runs as the virtual account <c>NT SERVICE\SolvixAgent</c> with only
/// the privileges it needs; the data directory is writable only by that account, SYSTEM and
/// Administrators; only the updater scheduled task runs as SYSTEM and it installs nothing that has not
/// passed the hash + publisher checks again.
/// </summary>
public sealed class AgentInstaller(ICommandRunner runner, Action<string>? progress = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceAlreadyRunning = 1056;
    private const int ServiceNotActive = 1062;
    private const string SystemSid = "*S-1-5-18";
    private const string AdministratorsSid = "*S-1-5-32-544";
    private const string UsersSid = "*S-1-5-32-545";

    private readonly Action<string> _progress = progress ?? (_ => { });
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static string? CheckEnvironment(InstallEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!environment.Is64BitOs)
        {
            return "The Solvix agent requires 64-bit Windows.";
        }

        return WindowsVersionInfo.IsSupported(environment.OsBuild, environment.ProductType)
            ? null
            : $"Unsupported Windows version: {environment.Caption} (build {environment.OsBuild}). Supported: Windows 10, Windows 11, Windows Server 2016 or later.";
    }

    public async Task<InstallResult> InstallAsync(InstallOptions options, InstallEnvironment environment, Action configureEnrollment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configureEnrollment);
        if (CheckEnvironment(environment) is { } unsupported)
        {
            return InstallResult.Fail(unsupported);
        }

        var exe = options.ExePath;
        var service = options.ServiceName;

        // 1. Stop a previous installation so its files can be replaced.
        _progress("Checking for an existing installation");
        var existing = await runner.RunAsync("sc.exe", $"query {service}", cancellationToken);
        var exists = existing.ExitCode == 0;
        if (exists)
        {
            _progress("Stopping the existing Solvix Agent service");
            await runner.RunAsync("sc.exe", $"stop {service}", cancellationToken);
            if (!await WaitForStoppedAsync(service, cancellationToken))
            {
                return InstallResult.Fail("The existing Solvix Agent service did not stop.");
            }
        }

        // 2. Program files.
        _progress($"Copying the agent to {options.InstallDirectory}");
        Directory.CreateDirectory(options.InstallDirectory);
        if (!SamePath(options.SourceExePath, exe))
        {
            File.Copy(options.SourceExePath, exe, overwrite: true);
            var settings = Path.Combine(Path.GetDirectoryName(options.SourceExePath)!, "appsettings.json");
            if (File.Exists(settings))
            {
                File.Copy(settings, Path.Combine(options.InstallDirectory, "appsettings.json"), overwrite: true);
            }
        }

        // The updater task runs this binary as SYSTEM: only SYSTEM and Administrators may change it.
        var step = await RunAsync("icacls.exe", $"\"{options.InstallDirectory}\" /inheritance:r /grant:r {SystemSid}:(OI)(CI)F {AdministratorsSid}:(OI)(CI)F {UsersSid}:(OI)(CI)RX", "protecting the install directory", cancellationToken);
        if (step is not null)
        {
            return step;
        }

        // 3. Data directory: SYSTEM + Administrators only for now; the service account is added once it exists.
        _progress("Securing the data directory");
        Directory.CreateDirectory(options.DataDirectory);
        step = await RunAsync("icacls.exe", $"\"{options.DataDirectory}\" /inheritance:r /grant:r {SystemSid}:(OI)(CI)F {AdministratorsSid}:(OI)(CI)F", "securing the data directory", cancellationToken);
        if (step is not null)
        {
            return step;
        }

        // 4. Event log source (warnings and errors are mirrored there).
        await runner.RunAsync("eventcreate.exe", $"/L APPLICATION /T INFORMATION /SO \"{options.EventSource}\" /ID 100 /D \"Solvix Agent installed\"", cancellationToken);

        // 5. Service.
        _progress("Registering the Solvix Agent service");
        if (!exists)
        {
            step = await RunAsync("sc.exe", $"create {service} binPath= \"\\\"{exe}\\\"\" DisplayName= \"Solvix Agent\" start= delayed-auto obj= \"{options.ServiceAccount}\"", "creating the service", cancellationToken);
        }
        else
        {
            step = await RunAsync("sc.exe", $"config {service} binPath= \"\\\"{exe}\\\"\" start= delayed-auto obj= \"{options.ServiceAccount}\"", "updating the service", cancellationToken);
        }

        if (step is not null)
        {
            return step;
        }

        foreach (var arguments in new[]
        {
            $"description {service} \"Reports device inventory and health to Solvix EndPoints. Outbound HTTPS only.\"",
            $"sidtype {service} unrestricted",
            $"privs {service} SeChangeNotifyPrivilege/SeCreateGlobalPrivilege",
            $"failure {service} reset= 86400 actions= restart/60000/restart/60000/restart/300000",
            $"failureflag {service} 1",
        })
        {
            step = await RunAsync("sc.exe", arguments, "configuring the service", cancellationToken);
            if (step is not null)
            {
                return step;
            }
        }

        step = await RunAsync("icacls.exe", $"\"{options.DataDirectory}\" /grant:r \"{options.ServiceAccount}:(OI)(CI)M\"", "granting the service account access to its data", cancellationToken);
        if (step is not null)
        {
            return step;
        }

        // 6. Privileged updater: installs only staged packages that pass hash + publisher checks again.
        _progress("Registering the updater task");
        foreach (var (name, schedule) in new[] { ("Solvix Agent Updater", "/SC HOURLY"), ("Solvix Agent Updater (startup)", "/SC ONSTART") })
        {
            step = await RunAsync("schtasks.exe", $"/Create /F /TN \"{options.TaskFolder}{name}\" /TR \"\\\"{exe}\\\" apply-update\" {schedule} /RU SYSTEM /RL HIGHEST", "registering the updater task", cancellationToken);
            if (step is not null)
            {
                return step;
            }
        }

        // 7. Enrollment settings + one-time token, then start.
        _progress("Storing the enrollment settings");
        configureEnrollment();

        _progress("Starting the service");
        var start = await runner.RunAsync("sc.exe", $"start {service}", cancellationToken);
        if (start.ExitCode is not (0 or ServiceAlreadyRunning))
        {
            return InstallResult.Fail($"The service was installed but could not be started (sc.exe exit {start.ExitCode}): {start.Output}");
        }

        return InstallResult.Ok("Solvix Agent installed and started. It enrolls within a minute; the device then appears in Solvix under Endpoints → Devices.");
    }

    public async Task<InstallResult> UninstallAsync(InstallOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var service = options.ServiceName;
        _progress("Stopping the service");
        var query = await runner.RunAsync("sc.exe", $"query {service}", cancellationToken);
        if (query.ExitCode == 0)
        {
            await runner.RunAsync("sc.exe", $"stop {service}", cancellationToken);
            await WaitForStoppedAsync(service, cancellationToken);
            var delete = await runner.RunAsync("sc.exe", $"delete {service}", cancellationToken);
            if (delete.ExitCode != 0)
            {
                return InstallResult.Fail($"The service could not be deleted (sc.exe exit {delete.ExitCode}): {delete.Output}");
            }
        }

        _progress("Removing the updater tasks");
        foreach (var name in new[] { "Solvix Agent Updater", "Solvix Agent Updater (startup)" })
        {
            await runner.RunAsync("schtasks.exe", $"/Delete /F /TN \"{options.TaskFolder}{name}\"", cancellationToken);
        }

        return InstallResult.Ok("Solvix Agent service removed.");
    }

    public async Task<bool> IsInstalledAsync(string serviceName, CancellationToken cancellationToken) =>
        (await runner.RunAsync("sc.exe", $"query {serviceName}", cancellationToken)).ExitCode == 0;

    private async Task<InstallResult?> RunAsync(string fileName, string arguments, string what, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(fileName, arguments, cancellationToken);
        return result.ExitCode == 0 ? null : InstallResult.Fail($"Failed while {what} ({fileName} exit {result.ExitCode}): {result.Output}");
    }

    private async Task<bool> WaitForStoppedAsync(string service, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var query = await runner.RunAsync("sc.exe", $"query {service}", cancellationToken);
            if (query.ExitCode is ServiceDoesNotExist or ServiceNotActive || query.Output.Contains("STOPPED", StringComparison.Ordinal))
            {
                return true;
            }

            await _delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
