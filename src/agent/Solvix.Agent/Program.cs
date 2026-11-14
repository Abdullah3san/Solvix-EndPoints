// Solvix Windows endpoint agent.
//
//   Solvix.Agent.exe                         double-click: install + enroll from enrollment.json
//                                            (as a service: run; on Linux/dev: run in the console)
//   Solvix.Agent.exe install [--silent]      install as a Windows service and enroll (UAC prompt);
//                                            reads enrollment.json next to the exe, or --server/--token/--pin
//   Solvix.Agent.exe uninstall [--remove-data]   remove the service (and optionally the device identity)
//   Solvix.Agent.exe run                     run in the console
//   Solvix.Agent.exe enroll [--server <url> --token <token> [--pin <sha256>]]
//                                            store server URL + protected one-time token
//                                            (no arguments: read enrollment.json next to the exe)
//   Solvix.Agent.exe status                  print local enrollment / health state (diagnostics)
//   Solvix.Agent.exe inventory               collect inventory locally and print it (no network)
//   Solvix.Agent.exe apply-update            privileged updater (scheduled task as SYSTEM)
//   Solvix.Agent.exe reset                   delete the device key, certificate and state (uninstall)
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Solvix.Agent;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Enrollment;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;
using Solvix.Agent.Core.Updates;
using Solvix.Agent.Windows;

// No command: a person double-clicking the exe on Windows gets the installer; the Windows service
// manager, Linux and development hosts get the agent itself.
var interactiveWindows = OperatingSystem.IsWindows()
    && Environment.UserInteractive
    && !Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService();
var command = args.FirstOrDefault(a => !a.StartsWith('-'))?.ToLowerInvariant() ?? (interactiveWindows ? "setup" : "run");
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// Configuration: defaults next to the binary, machine settings written at install time, then environment.
var bootstrapOptions = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
var dataDirectory = bootstrapOptions.ResolveDataDirectory();
var machineSettings = Path.Combine(dataDirectory, "agent.json");
builder.Configuration.AddJsonFile(machineSettings, optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables("SOLVIX_AGENT_");

// Local diagnostic logging: rolling files in the data directory + Windows Event Log for warnings.
var logging = builder.Configuration.GetSection("Logging");
var logConfiguration = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<LogEventLevel>(logging["MinimumLevel"], true, out var level) ? level : LogEventLevel.Information)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("AgentVersion", AgentInfo.Version)
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(dataDirectory, "logs", "agent-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: logging.GetValue("RetainedFileCount", 14),
        fileSizeLimitBytes: logging.GetValue("FileSizeLimitBytes", 10L * 1024 * 1024),
        rollOnFileSizeLimit: true,
        shared: true);
if (Environment.UserInteractive)
{
    logConfiguration.WriteTo.Console(restrictedToMinimumLevel: command == "run" ? LogEventLevel.Information : LogEventLevel.Warning);
}

if (OperatingSystem.IsWindows())
{
    // The event source is registered by the installer (needs administrative rights).
    logConfiguration.WriteTo.EventLog("Solvix Agent", manageEventSource: false, restrictedToMinimumLevel: LogEventLevel.Warning);
}

Log.Logger = logConfiguration.CreateLogger();
builder.Services.AddSerilog();

builder.Services.AddSolvixAgentCore(builder.Configuration);
if (OperatingSystem.IsWindows())
{
    builder.Services.AddSolvixAgentWindows(dataDirectory);
    builder.Services.AddWindowsService(o => o.ServiceName = "SolvixAgent");
}
else
{
    // Development on non-Windows hosts only.
    builder.Services.AddSingleton<IDeviceKeyStore>(_ => new FileDeviceKeyStore(dataDirectory));
    builder.Services.AddSingleton<ISecretProtector, FilePermissionSecretProtector>();
    builder.Services.AddSingleton<IPackageInstaller, UnsupportedInstaller>();
}

if (command == "run")
{
    builder.Services.AddHostedService<AgentWorker>();
}

try
{
    using var host = builder.Build();
    return command switch
    {
        "run" => await RunAsync(host),
        "setup" => await SetupCommands.SetupAsync(host, dataDirectory),
        "install" => await SetupCommands.InstallAsync(host, args, dataDirectory),
        "uninstall" => await SetupCommands.UninstallAsync(host, args, dataDirectory),
        "enroll" => Commands.Enroll(host, args),
        "status" => Commands.Status(host),
        "inventory" => await Commands.InventoryAsync(host),
        "apply-update" => await Commands.ApplyUpdateAsync(host),
        "reset" => Commands.Reset(host, dataDirectory),
        _ => Commands.Usage(),
    };
}
catch (Exception ex)
{
    Log.Fatal(ex, "Solvix agent terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunAsync(IHost host)
{
    await host.RunAsync();
    return 0;
}

namespace Solvix.Agent
{
    internal static class Commands
    {
        private static readonly JsonSerializerOptions Pretty = new(AgentJsonContext.Default.Options) { WriteIndented = true };

        public static int Enroll(IHost host, string[] args)
        {
            var file = Path.Combine(AppContext.BaseDirectory, AgentEnrollmentFile.FileName);
            var server = Option(args, "--server");
            var token = Option(args, "--token") ?? Environment.GetEnvironmentVariable("SOLVIX_ENROLLMENT_TOKEN");
            var fromFile = server is null && token is null && File.Exists(file);

            EnrollmentRequestSettings? settings;
            string? error;
            if (fromFile)
            {
                settings = EnrollmentRequestSettings.TryLoad(file, DateTimeOffset.UtcNow, out error);
            }
            else
            {
                settings = EnrollmentRequestSettings.Validate(server, token, Option(args, "--pin"), out error);
            }

            if (settings is null)
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine($"Usage: Solvix.Agent enroll [--server https://<host>[:port]/ --token <token> [--pin <sha256>]]  (no arguments: read {AgentEnrollmentFile.FileName} next to the exe)");
                return 2;
            }

            host.Services.GetRequiredService<EnrollmentSetup>().Apply(settings);
            if (fromFile)
            {
                File.Delete(file); // the token now lives encrypted in the data directory
            }

            Console.WriteLine("Enrollment configured. The Solvix Agent enrolls on its next start.");
            return 0;
        }

        public static int Status(IHost host)
        {
            var state = host.Services.GetRequiredService<IAgentStateStore>().Load();
            var bootstrap = host.Services.GetRequiredService<BootstrapTokenStore>();
            var keys = host.Services.GetRequiredService<IDeviceKeyStore>();
            Console.WriteLine($"Agent version:        {AgentInfo.Version}");
            Console.WriteLine($"Enrolled:             {state.IsEnrolled}");
            Console.WriteLine($"Device id:            {state.DeviceId?.ToString() ?? "-"}");
            Console.WriteLine($"Tenant id:            {state.TenantId?.ToString() ?? "-"}");
            Console.WriteLine($"Server:               {state.ServerUrl?.ToString() ?? "-"}");
            Console.WriteLine($"Key storage:          {keys.Description}");
            Console.WriteLine($"Certificate expires:  {state.CertificateNotAfterUtc?.ToString("u") ?? "-"}");
            Console.WriteLine($"Last contact:         {state.LastSuccessfulContactAtUtc?.ToString("u") ?? "-"}");
            Console.WriteLine($"Last inventory:       {state.LastInventoryAtUtc?.ToString("u") ?? "-"}");
            Console.WriteLine($"Enrollment token:     {(bootstrap.HasToken ? "pending" : bootstrap.HasRejectedToken ? "REJECTED by the server - run 'enroll' with a new token" : "none")}");
            return 0;
        }

        public static async Task<int> InventoryAsync(IHost host)
        {
            var report = await host.Services.GetRequiredService<IInventoryService>().CollectAsync(CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(report, Pretty));
            return 0;
        }

        public static async Task<int> ApplyUpdateAsync(IHost host)
        {
            var updates = host.Services.GetRequiredService<UpdateManager>();
            var installer = host.Services.GetRequiredService<IPackageInstaller>();
            return await updates.ApplyStagedAsync(installer, AgentInfo.Version, CancellationToken.None);
        }

        public static int Reset(IHost host, string dataDirectory)
        {
            host.Services.GetRequiredService<DeviceCredentialStore>().Reset();
            host.Services.GetRequiredService<BootstrapTokenStore>().Delete();
            foreach (var file in new[] { "state.json", "ca.crt", "agent.json" })
            {
                var path = Path.Combine(dataDirectory, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            host.Services.GetRequiredService<UpdateManager>().Discard();
            Console.WriteLine("Device credential and state removed.");
            return 0;
        }

        public static int Usage()
        {
            Console.Error.WriteLine("Commands: install | uninstall | run | enroll | status | inventory | apply-update | reset");
            return 2;
        }

        private static string? Option(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }

    internal sealed class UnsupportedInstaller : IPackageInstaller
    {
        public Task<int> InstallAsync(string packagePath, CancellationToken cancellationToken) =>
            throw new PlatformNotSupportedException("Package installation is only supported on Windows.");
    }
}
