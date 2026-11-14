namespace Solvix.Agent.Windows.Security;

using Solvix.Agent.Windows.Inventory;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;
using Solvix.Agent.Core.Installation;

/// <summary>UAC: detects administrator rights and relaunches the exe elevated ("runas").</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsElevation : IElevation
{
    private const int ErrorCancelled = 1223;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public int RelaunchElevated(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The executable path is unknown."))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The elevated process could not be started.");
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return ErrorCancelled; // the user declined the UAC prompt
        }
    }
}

/// <summary>OS facts for the installer's pre-flight check.</summary>
[SupportedOSPlatform("windows")]
public static class WindowsInstallEnvironment
{
    public static InstallEnvironment Detect()
    {
        var os = WmiFacade.OperatingSystem();
        return new InstallEnvironment(os.Build, os.ProductType, Environment.Is64BitOperatingSystem, os.Caption);
    }
}
