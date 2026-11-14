namespace Solvix.IntegrationTests.Fixtures;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Solvix.Modules.Mdm.Providers.Android;
using Solvix.Modules.Mdm.Providers.Apple;

/// <summary>
/// In-memory stand-in for the Android Management API. It records what Solvix sends and lets a test play the
/// part of Google and of the device (enroll a device, mark an operation done, delete a device).
/// </summary>
public sealed class FakeAndroidManagementApi : IAndroidManagementApi
{
    private int _counter;

    public ConcurrentDictionary<string, JsonObject> Policies { get; } = new();

    public ConcurrentDictionary<string, JsonObject> EnrollmentTokens { get; } = new();

    public ConcurrentDictionary<string, AndroidDeviceInfo> Devices { get; } = new();

    public ConcurrentQueue<(string Device, JsonObject Command)> Commands { get; } = new();

    public ConcurrentQueue<(string Device, string[] Flags)> Deletions { get; } = new();

    public ConcurrentQueue<(string Device, JsonObject Patch)> DevicePatches { get; } = new();

    public ConcurrentDictionary<string, AndroidOperationInfo> Operations { get; } = new();

    public bool CompleteOperationsImmediately { get; set; } = true;

    public Task<AndroidSignupUrl> CreateSignupUrlAsync(string projectId, string callbackUrl, CancellationToken cancellationToken) =>
        Task.FromResult(new AndroidSignupUrl($"signupUrls/{Interlocked.Increment(ref _counter)}", "https://play.google.com/work/adminsignup?token=fake"));

    public Task<AndroidEnterpriseInfo> CreateEnterpriseAsync(string projectId, string signupUrlName, string enterpriseToken, string displayName, CancellationToken cancellationToken) =>
        Task.FromResult(new AndroidEnterpriseInfo($"enterprises/LC{Guid.NewGuid():N}"[..20], displayName));

    public Task<AndroidEnrollmentTokenInfo> CreateEnrollmentTokenAsync(string enterpriseName, JsonObject token, CancellationToken cancellationToken)
    {
        var name = $"{enterpriseName}/enrollmentTokens/{Guid.NewGuid():N}";
        EnrollmentTokens[name] = (JsonObject)token.DeepClone();
        return Task.FromResult(new AndroidEnrollmentTokenInfo(name, "TOKEN" + Guid.NewGuid().ToString("N")[..8], "{\"qr\":true}", DateTimeOffset.UtcNow.AddDays(1)));
    }

    public Task PatchPolicyAsync(string policyName, JsonObject policy, CancellationToken cancellationToken)
    {
        Policies[policyName] = (JsonObject)policy.DeepClone();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AndroidDeviceInfo>> ListDevicesAsync(string enterpriseName, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AndroidDeviceInfo>>(Devices.Values.Where(d => d.Name.StartsWith(enterpriseName + "/", StringComparison.Ordinal)).ToList());

    public Task PatchDeviceAsync(string deviceName, JsonObject patch, string updateMask, CancellationToken cancellationToken)
    {
        DevicePatches.Enqueue((deviceName, (JsonObject)patch.DeepClone()));
        return Task.CompletedTask;
    }

    public Task<string> IssueCommandAsync(string deviceName, JsonObject command, CancellationToken cancellationToken)
    {
        Commands.Enqueue((deviceName, (JsonObject)command.DeepClone()));
        var operation = $"{deviceName}/operations/{Guid.NewGuid():N}";
        Operations[operation] = new AndroidOperationInfo(CompleteOperationsImmediately, null);
        return Task.FromResult(operation);
    }

    public Task<AndroidOperationInfo> GetOperationAsync(string operationName, CancellationToken cancellationToken) =>
        Task.FromResult(Operations.GetValueOrDefault(operationName) ?? new AndroidOperationInfo(false, null));

    public Task DeleteDeviceAsync(string deviceName, IReadOnlyList<string> wipeDataFlags, string? wipeReasonMessage, CancellationToken cancellationToken)
    {
        Deletions.Enqueue((deviceName, wipeDataFlags.ToArray()));
        if (Devices.TryGetValue(deviceName, out var device))
        {
            Devices[deviceName] = device with { State = "DELETED" };
        }

        return Task.CompletedTask;
    }

    public void Reset()
    {
        Policies.Clear();
        EnrollmentTokens.Clear();
        Devices.Clear();
        Commands.Clear();
        Deletions.Clear();
        DevicePatches.Clear();
        Operations.Clear();
    }
}

public sealed class RecordingApplePushClient : IApplePushClient
{
    public ConcurrentQueue<(string Token, string Magic, string Topic)> Pushes { get; } = new();

    public Task SendMdmPushAsync(string pushTokenHex, string pushMagic, string topic, CancellationToken cancellationToken)
    {
        Pushes.Enqueue((pushTokenHex, pushMagic, topic));
        return Task.CompletedTask;
    }
}
