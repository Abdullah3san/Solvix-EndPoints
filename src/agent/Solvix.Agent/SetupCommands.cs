namespace Solvix.Agent
{
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Solvix.Agent.Contracts;
    using Solvix.Agent.Core.Enrollment;
    using Solvix.Agent.Core.Installation;
    using Solvix.Agent.Core.Security;
    using Solvix.Agent.Windows.Security;

    /// <summary>
    /// <c>install</c> / <c>uninstall</c> / double-click setup. The administrator downloads a package
    /// (the signed exe + <c>enrollment.json</c> with a short-lived token) from Solvix, extracts it and
    /// runs the exe: no command line, no script.
    /// </summary>
    internal static class SetupCommands
    {
        private const string ServiceName = "SolvixAgent";
        private const int ErrorCancelled = 1223;

        public static async Task<int> SetupAsync(IHost host, string dataDirectory)
        {
            var file = Path.Combine(AppContext.BaseDirectory, AgentEnrollmentFile.FileName);
            if (File.Exists(file))
            {
                return await InstallAsync(host, [], dataDirectory);
            }

            var installer = new AgentInstaller(host.Services.GetRequiredService<ICommandRunner>());
            if (await installer.IsInstalledAsync(ServiceName, CancellationToken.None))
            {
                Console.WriteLine("Solvix Agent is already installed on this computer.");
                Console.WriteLine("  Status:    Solvix.Agent.exe status");
                Console.WriteLine("  Uninstall: Solvix.Agent.exe uninstall");
            }
            else
            {
                Console.WriteLine($"{AgentEnrollmentFile.FileName} was not found next to Solvix.Agent.exe.");
                Console.WriteLine("Download the agent package from Solvix (Endpoints > Enrollment > New enrollment token), extract the");
                Console.WriteLine("whole zip and run Solvix.Agent.exe from the extracted folder.");
            }

            Pause(silent: false);
            return 1;
        }

        public static async Task<int> InstallAsync(IHost host, string[] args, string dataDirectory)
        {
            var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
            try
            {
                var file = Option(args, "--file") ?? Path.Combine(AppContext.BaseDirectory, AgentEnrollmentFile.FileName);
                var fromFile = Option(args, "--server") is null && Option(args, "--token") is null;
                EnrollmentRequestSettings? settings;
                string? error;
                if (fromFile)
                {
                    if (!File.Exists(file))
                    {
                        return Fail($"{AgentEnrollmentFile.FileName} was not found. Use the package downloaded from Solvix, or pass --server and --token.", silent);
                    }

                    settings = EnrollmentRequestSettings.TryLoad(file, DateTimeOffset.UtcNow, out error);
                }
                else
                {
                    settings = EnrollmentRequestSettings.Validate(
                        Option(args, "--server"),
                        Option(args, "--token") ?? Environment.GetEnvironmentVariable("SOLVIX_ENROLLMENT_TOKEN"),
                        Option(args, "--pin"),
                        out error);
                }

                if (settings is null)
                {
                    return Fail(error ?? "Invalid enrollment settings.", silent);
                }

                var elevation = host.Services.GetRequiredService<IElevation>();
                if (!elevation.IsElevated)
                {
                    Console.WriteLine("Installing the Solvix Agent needs administrator rights. Please approve the Windows prompt.");
                    var forwarded = new List<string> { "install", "--file", Path.GetFullPath(file) };
                    forwarded.AddRange(args.Where(a => !string.Equals(a, "install", StringComparison.OrdinalIgnoreCase)));
                    var code = elevation.RelaunchElevated(forwarded);
                    return code == ErrorCancelled ? Fail("Installation cancelled: administrator approval is required.", silent) : code;
                }

                if (!OperatingSystem.IsWindows())
                {
                    return Fail("The installer is only supported on Windows.", silent);
                }

                var environment = Solvix.Agent.Windows.Security.WindowsInstallEnvironment.Detect();
                var installer = new AgentInstaller(host.Services.GetRequiredService<ICommandRunner>(), Console.WriteLine);
                var options = new InstallOptions
                {
                    SourceExePath = Environment.ProcessPath!,
                    InstallDirectory = Option(args, "--install-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Solvix", "Agent"),
                    DataDirectory = dataDirectory,
                };

                Console.WriteLine("Installing the Solvix Agent");
                Console.WriteLine($"  Server: {settings.ServerUrl}");
                var enrollment = host.Services.GetRequiredService<EnrollmentSetup>();
                var result = await installer.InstallAsync(options, environment, () => enrollment.Apply(settings), CancellationToken.None);
                if (!result.Success)
                {
                    return Fail(result.Message, silent);
                }

                if (fromFile && File.Exists(file))
                {
                    File.Delete(file); // the one-time token is now stored encrypted by the agent
                }

                Console.WriteLine(result.Message);
                Pause(silent);
                return 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return Fail(ex.Message, silent);
            }
        }

        public static async Task<int> UninstallAsync(IHost host, string[] args, string dataDirectory)
        {
            var silent = args.Contains("--silent", StringComparer.OrdinalIgnoreCase);
            var removeData = args.Contains("--remove-data", StringComparer.OrdinalIgnoreCase);
            var elevation = host.Services.GetRequiredService<IElevation>();
            if (!elevation.IsElevated)
            {
                var code = elevation.RelaunchElevated(["uninstall", .. args.Where(a => !string.Equals(a, "uninstall", StringComparison.OrdinalIgnoreCase))]);
                return code == ErrorCancelled ? Fail("Uninstall cancelled: administrator approval is required.", silent) : code;
            }

            var installDirectory = Option(args, "--install-dir") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Solvix", "Agent");
            var installer = new AgentInstaller(host.Services.GetRequiredService<ICommandRunner>(), Console.WriteLine);
            var result = await installer.UninstallAsync(new InstallOptions { SourceExePath = Environment.ProcessPath ?? "Solvix.Agent.exe", InstallDirectory = installDirectory, DataDirectory = dataDirectory }, CancellationToken.None);
            if (!result.Success)
            {
                return Fail(result.Message, silent);
            }

            if (removeData)
            {
                host.Services.GetRequiredService<DeviceCredentialStore>().Reset(); // deletes the CNG/TPM device key
                if (Directory.Exists(dataDirectory))
                {
                    Directory.Delete(dataDirectory, recursive: true);
                }
            }

            RemoveInstallDirectory(installDirectory);
            Console.WriteLine(result.Message + (removeData ? " Device identity and data removed." : " Device identity kept (use --remove-data to delete it)."));
            Console.WriteLine("The device record stays in Solvix until an administrator retires or deletes it.");
            Pause(silent);
            return 0;
        }

        private static void RemoveInstallDirectory(string installDirectory)
        {
            if (!Directory.Exists(installDirectory))
            {
                return;
            }

            var running = Environment.ProcessPath is { } path && Path.GetFullPath(path).StartsWith(Path.GetFullPath(installDirectory), StringComparison.OrdinalIgnoreCase);
            if (!running)
            {
                Directory.Delete(installDirectory, recursive: true);
                return;
            }

            // A running exe cannot delete itself: a detached helper removes the folder after this process exits.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 4 127.0.0.1 >nul & rmdir /s /q \"{installDirectory}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }

        private static int Fail(string message, bool silent)
        {
            Console.Error.WriteLine("ERROR: " + message);
            Pause(silent);
            return 1;
        }

        /// <summary>Keeps the console window open when started by a double-click (including the elevated copy).</summary>
        private static void Pause(bool silent)
        {
            if (silent || !Environment.UserInteractive || Console.IsInputRedirected)
            {
                return;
            }

            Console.WriteLine();
            Console.Write("Press any key to close this window...");
            Console.ReadKey(intercept: true);
        }

        private static string? Option(string[] args, string name)
        {
            var index = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }
    }
}
