namespace Solvix.Agent.Windows.Platform;

using System.Runtime.InteropServices;
using System.ServiceProcess;
using Solvix.Agent.Core.Patching;
using Solvix.Endpoint.Contracts.Agent;
using Solvix.Endpoint.Contracts.Patching;

/// <summary>
/// Windows Update Agent API (Microsoft.Update.Session and friends) via COM late
/// binding. Works with Windows Update, Microsoft Update and WSUS — whatever the
/// device is configured for — so existing update sources and Group Policy keep working.
/// </summary>
internal sealed class WindowsUpdateClient : IWindowsUpdateClient
{
    private const string ClientId = "Solvix Agent";
    private const int MaxHistory = 500;

    /// <summary>Language-independent ids of the WUA "UpdateClassification" categories.</summary>
    private static readonly Dictionary<Guid, UpdateClassification> Classifications = new()
    {
        [new Guid("E6CF1350-C01B-414D-A61F-263D14D133B4")] = UpdateClassification.CriticalUpdates,
        [new Guid("0FA1201D-4330-4FA8-8AE9-B877473B6441")] = UpdateClassification.SecurityUpdates,
        [new Guid("E0789628-CE08-4437-BE74-2495B842F43B")] = UpdateClassification.DefinitionUpdates,
        [new Guid("28BC880E-0592-4CBF-8F95-C79B17911D5F")] = UpdateClassification.UpdateRollups,
        [new Guid("68C5B0A3-D1A6-4553-AE49-01D3A7827828")] = UpdateClassification.ServicePacks,
        [new Guid("B54E7D24-7ADD-428F-8B75-90A396FA584F")] = UpdateClassification.FeaturePacks,
        [new Guid("B4832BD8-E735-4761-8DAF-37F882276DAB")] = UpdateClassification.Tools,
        [new Guid("EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0")] = UpdateClassification.Drivers,
        [new Guid("CD5FFD1E-E932-4E3A-BF74-18BF0B1BBD83")] = UpdateClassification.Updates,
        [new Guid("3689BDC8-B205-4AF4-8D4A-A63924C5E9D5")] = UpdateClassification.Upgrades,
    };

    private readonly RebootManager _reboot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsUpdateClient(RebootManager reboot) => _reboot = reboot;

    public Task<PatchInventoryReport> ScanAsync(CancellationToken cancellationToken) => RunExclusiveAsync(() =>
    {
        var started = DateTimeOffset.UtcNow;
        var service = ServiceStatus();
        try
        {
            var session = Session();
            var searcher = session.CreateUpdateSearcher();
            var result = searcher.Search("IsInstalled=0 and Type='Software' or IsInstalled=1 and Type='Software'");
            var updates = new List<UpdateInfo>();
            for (var i = 0; i < (int)result.Updates.Count; i++)
            {
                updates.Add(ToInfo(result.Updates.Item(i)));
            }

            var history = new List<UpdateHistoryEntry>();
            var total = (int)searcher.GetTotalHistoryCount();
            if (total > 0)
            {
                var entries = searcher.QueryHistory(0, Math.Min(total, MaxHistory));
                for (var i = 0; i < (int)entries.Count; i++)
                {
                    var e = entries.Item(i);
                    var operation = (int)e.Operation;
                    if (operation is not (1 or 2) || string.IsNullOrEmpty((string?)e.UpdateIdentity.UpdateID))
                    {
                        continue;
                    }

                    history.Add(new UpdateHistoryEntry(
                        (string)e.UpdateIdentity.UpdateID,
                        (int)e.UpdateIdentity.RevisionNumber,
                        (string?)e.Title ?? string.Empty,
                        (UpdateOperation)operation,
                        (OperationResultCode)(int)e.ResultCode,
                        (int)e.HResult,
                        new DateTimeOffset(DateTime.SpecifyKind((DateTime)e.Date, DateTimeKind.Utc))));
                }
            }

            return new PatchInventoryReport(
                started,
                DateTimeOffset.UtcNow,
                (OperationResultCode)(int)result.ResultCode,
                null,
                null,
                service,
                _reboot.GetRebootStatus(),
                updates,
                history);
        }
        catch (COMException ex)
        {
            return new PatchInventoryReport(started, DateTimeOffset.UtcNow, OperationResultCode.Failed, ex.HResult, ex.Message, service, _reboot.GetRebootStatus(), [], []);
        }
    }, cancellationToken);

    public Task<UpdateStatus> GetStatusAsync(PatchJobUpdate update, CancellationToken cancellationToken) => RunExclusiveAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(update);
        var found = Find(Session(), update);
        return found is null
            ? new UpdateStatus(false, false, false)
            : new UpdateStatus(true, (bool)found.IsInstalled, (bool)found.RebootRequired);
    }, cancellationToken);

    public Task<UpdateInstallOutcome> InstallAsync(PatchJobUpdate update, CancellationToken cancellationToken) => RunExclusiveAsync(() =>
    {
        ArgumentNullException.ThrowIfNull(update);
        var session = Session();
        var found = Find(session, update) ?? throw new InvalidOperationException($"Update {update.UpdateId} rev {update.RevisionNumber} is not offered to this device.");
        if (!(bool)found.EulaAccepted)
        {
            found.AcceptEula();
        }

        var collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl", throwOnError: true)!)!;
        ((dynamic)collection).Add(found);
        if (!(bool)found.IsDownloaded)
        {
            var downloader = session.CreateUpdateDownloader();
            downloader.Updates = collection;
            var download = downloader.Download();
            var downloadCode = (OperationResultCode)(int)download.ResultCode;
            if (downloadCode is not (OperationResultCode.Succeeded or OperationResultCode.SucceededWithErrors))
            {
                return new UpdateInstallOutcome(downloadCode == OperationResultCode.Aborted ? OperationResultCode.Aborted : OperationResultCode.Failed, (int)download.HResult, false);
            }
        }

        var installer = session.CreateUpdateInstaller();
        installer.Updates = collection;
        installer.ForceQuiet = true;
        installer.AllowSourcePrompts = false;
        var install = installer.Install();
        var result = install.GetUpdateResult(0);
        return new UpdateInstallOutcome((OperationResultCode)(int)result.ResultCode, (int)result.HResult, (bool)result.RebootRequired || (bool)install.RebootRequired);
    }, cancellationToken);

    private static dynamic Session()
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
        session.ClientApplicationID = ClientId;
        return session;
    }

    private static dynamic? Find(dynamic session, PatchJobUpdate update)
    {
        if (!Guid.TryParse(update.UpdateId, out var id))
        {
            return null;
        }

        var searcher = session.CreateUpdateSearcher();
        var result = searcher.Search($"UpdateID='{id:D}' and RevisionNumber={update.RevisionNumber}");
        return (int)result.Updates.Count > 0 ? result.Updates.Item(0) : null;
    }

    private static UpdateInfo ToInfo(dynamic u)
    {
        var classification = UpdateClassification.Unknown;
        foreach (var category in u.Categories)
        {
            if ((string)category.Type == "UpdateClassification" && Guid.TryParse((string)category.CategoryID, out Guid categoryId)
                && Classifications.TryGetValue(categoryId, out var mapped))
            {
                classification = mapped;
            }
        }

        var severity = Enum.TryParse<MsrcSeverity>((string?)u.MsrcSeverity, ignoreCase: true, out var s) ? s : MsrcSeverity.Unspecified;
        DateTime? changed = u.LastDeploymentChangeTime is DateTime d ? d : null;
        return new UpdateInfo(
            (string)u.Identity.UpdateID,
            (int)u.Identity.RevisionNumber,
            (string)u.Title,
            Strings(u.KBArticleIDs),
            classification,
            severity,
            changed is { } c ? new DateTimeOffset(DateTime.SpecifyKind(c, DateTimeKind.Utc)) : (DateTimeOffset?)null,
            (long?)(decimal)u.MaxDownloadSize,
            (RebootBehavior)(int)u.InstallationBehavior.RebootBehavior,
            (bool)u.IsInstalled,
            (bool)u.IsDownloaded,
            (bool)u.IsHidden,
            (bool)u.IsMandatory,
            (bool)u.RebootRequired,
            (string?)u.SupportUrl,
            Strings(u.CveIDs));
    }

    private static List<string> Strings(dynamic collection)
    {
        var list = new List<string>();
        for (var i = 0; i < (int)collection.Count; i++)
        {
            list.Add((string)collection.Item(i));
        }

        return list;
    }

    private static UpdateServiceStatus ServiceStatus()
    {
        string state;
        try
        {
            using var service = new ServiceController("wuauserv");
            state = service.Status.ToString();
        }
        catch (InvalidOperationException)
        {
            state = "NotInstalled";
        }

        string? level = null;
        DateTimeOffset? lastSearch = null;
        DateTimeOffset? lastInstall = null;
        try
        {
            dynamic au = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", throwOnError: true)!)!;
            level = ((int)au.Settings.NotificationLevel) switch
            {
                1 => "Disabled",
                2 => "NotifyBeforeDownload",
                3 => "NotifyBeforeInstallation",
                4 => "ScheduledInstallation",
                _ => "NotConfigured",
            };
            lastSearch = au.Results.LastSearchSuccessDate is DateTime ls ? new DateTimeOffset(DateTime.SpecifyKind(ls, DateTimeKind.Utc)) : null;
            lastInstall = au.Results.LastInstallationSuccessDate is DateTime li ? new DateTimeOffset(DateTime.SpecifyKind(li, DateTimeKind.Utc)) : null;
        }
        catch (COMException)
        {
            // AutoUpdate is unavailable on some SKUs; the remaining data is still useful.
        }

        var wsus = Providers.RegistryHelper.Read(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "UseWUServer").Value == "1";
        return new UpdateServiceStatus(state, level, wsus ? "WSUS" : "WindowsUpdate", lastSearch, lastInstall);
    }

    /// <summary>WUA serialises operations anyway; one at a time keeps COM objects on a dedicated thread.</summary>
    private async Task<T> RunExclusiveAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Factory.StartNew(operation, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
