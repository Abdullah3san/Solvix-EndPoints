// Solvix endpoint agent (Windows service "SolvixAgent", runs as LocalSystem).
//
//   Solvix.Agent.exe enroll --server https://solvix.example.com --token sxe_...
//       One-shot enrolment: stores the device credential (DPAPI) and exits. The
//       enrolment token is used once and never written to disk.
//   Solvix.Agent.exe
//       Runs the agent (as a Windows service when started by the SCM).
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Core.Api;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Patching;
using Solvix.Agent.Core.Runtime;
using Solvix.Agent.Windows;
using Solvix.Agent.Windows.Platform;
using Solvix.Agent.Windows.Providers;

var enrollOnly = args.Length > 0 && string.Equals(args[0], "enroll", StringComparison.OrdinalIgnoreCase);
var builder = Host.CreateApplicationBuilder(enrollOnly ? args[1..] : args);
builder.Configuration.AddCommandLine(enrollOnly ? args[1..] : args, new Dictionary<string, string>
{
    ["--server"] = $"{AgentOptions.SectionName}:ServerUrl",
    ["--token"] = $"{AgentOptions.SectionName}:EnrollmentToken",
});
builder.Services.AddWindowsService(o => o.ServiceName = "SolvixAgent");
builder.Logging.AddEventLog(o => o.SourceName = "Solvix Agent");

var options = builder.Configuration.GetSection(AgentOptions.SectionName).Get<AgentOptions>() ?? new AgentOptions();
if (options.ServerUrl is not { } server || !(server.Scheme == Uri.UriSchemeHttps || server.IsLoopback))
{
    Console.Error.WriteLine("Solvix:Agent:ServerUrl must be an https:// URL.");
    return 2;
}

StateDirectory.Secure(options.StateDirectory);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ISecretProtector, DpapiSecretProtector>();
builder.Services.AddSingleton(sp => new AgentStateStore(options.StateDirectory, sp.GetRequiredService<ISecretProtector>()));
builder.Services.AddSingleton<IAppliedSettingsLedger>(sp => sp.GetRequiredService<AgentStateStore>());
builder.Services.AddSingleton<ISnapshotStore>(_ => new FileSnapshotStore(Path.Combine(options.StateDirectory, "snapshots")));
builder.Services.AddSingleton<IPatchJobJournal>(_ => new FilePatchJobJournal(Path.Combine(options.StateDirectory, "patch-job.json")));
builder.Services.AddHttpClient<IAgentApi, AgentApiClient>(client =>
    {
        client.BaseAddress = new Uri(server.ToString().TrimEnd('/') + "/");
        client.Timeout = TimeSpan.FromMinutes(2);
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SolvixAgent/{AgentRuntime.AgentVersion}");
    })
    .AddTypedClient<IAgentApi>((http, sp) => new AgentApiClient(http, sp.GetRequiredService<AgentStateStore>().GetCredential));

builder.Services.AddSingleton<RebootManager>();
builder.Services.AddSingleton<IRebootManager>(sp => sp.GetRequiredService<RebootManager>());
builder.Services.AddSingleton<IWindowsUpdateClient, WindowsUpdateClient>();
builder.Services.AddSingleton<IDeviceInfoProvider, WindowsDeviceInfoProvider>();
builder.Services.AddSingleton<ISettingProvider, ServiceSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, RegistrySettingProvider>();
builder.Services.AddSingleton<ISettingProvider, StartupAppSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, FirewallProfileSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, FirewallRuleSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, PowerSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, ScreenLockSettingProvider>();
builder.Services.AddSingleton<ISettingProvider, UsbStorageSettingProvider>();
builder.Services.AddSingleton(sp => new ConfigurationEvaluator(
    sp.GetServices<ISettingProvider>(),
    sp.GetRequiredService<ISnapshotStore>(),
    sp.GetRequiredService<IAppliedSettingsLedger>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<ConfigurationEvaluator>>())
{
    MaxAllowedRisk = options.MaxAllowedRisk,
});
builder.Services.AddSingleton<PatchJobRunner>();
builder.Services.AddSingleton<AgentRuntime>();
builder.Services.AddHostedService<AgentWorker>();

using var host = builder.Build();
if (enrollOnly)
{
    var runtime = host.Services.GetRequiredService<AgentRuntime>();
    await runtime.EnsureEnrolledAsync(CancellationToken.None);
    Console.WriteLine($"Enrolled. Device id: {host.Services.GetRequiredService<AgentStateStore>().DeviceId}");
    return 0;
}

await host.RunAsync();
return 0;
