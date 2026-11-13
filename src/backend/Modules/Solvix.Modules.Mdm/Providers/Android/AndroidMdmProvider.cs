namespace Solvix.Modules.Mdm.Providers.Android;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;

/// <summary>Android Enterprise provider built on the Android Management API.</summary>
public sealed class AndroidMdmProvider(IAndroidManagementApi api, IOptions<MdmOptions> options) : IMdmProvider
{
    public MobilePlatform Platform => MobilePlatform.Android;

    public bool IsConfigured => options.Value.Android.IsConfigured;

    public async Task<EnrollmentArtifact> CreateEnrollmentAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var enterprise = request.EnterpriseName ?? throw new ConflictException("Bind an Android Enterprise before issuing enrollments.");
        var scenario = request.Scenario;
        var lifetime = Math.Max(60, (long)(request.ExpiresAtUtc - DateTimeOffset.UtcNow).TotalSeconds);
        var body = new JsonObject
        {
            ["policyName"] = AndroidPolicyTranslator.PolicyName(enterprise, request.PolicyId),
            ["duration"] = $"{lifetime}s",
            // The token id travels with the device (Device.enrollmentTokenData) so it can be matched without guessing.
            ["additionalData"] = request.TokenId.ToString("N"),
            ["oneTimeOnly"] = true,
            ["allowPersonalUsage"] = scenario.Mode == ManagementMode.WorkProfile ? "PERSONAL_USAGE_ALLOWED" : "PERSONAL_USAGE_DISALLOWED",
        };

        var token = await api.CreateEnrollmentTokenAsync(enterprise, body, cancellationToken);
        var instructions = scenario.Mode switch
        {
            ManagementMode.FullyManaged => new[]
            {
                "Factory reset the device, then tap the welcome screen six times and scan the QR code.",
                "The device becomes fully managed; there is no personal profile.",
            },
            ManagementMode.Dedicated => new[]
            {
                "Factory reset the device, then tap the welcome screen six times and scan the QR code.",
                "The device is locked to the kiosk apps defined in the policy.",
            },
            _ when scenario.IsPersonalBoundary => new[]
            {
                "On the user's device install 'Android Device Policy' from Google Play, open it and enter the enrollment token (or scan the QR code).",
                "A work profile is created. Personal apps and data stay private and are not visible to the organisation.",
            },
            _ => new[]
            {
                "Factory reset the device, then tap the welcome screen six times and scan the QR code.",
                "A work profile is created next to the user's personal profile.",
            },
        };

        return new EnrollmentArtifact
        {
            QrPayload = token.QrCode,
            TokenValue = token.Value,
            ProviderReference = token.Name,
            Instructions = instructions,
        };
    }

    public async Task<PolicyDeploymentReport> DeployPolicyAsync(PolicyDeployment deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        var enterprise = deployment.EnterpriseName ?? throw new ConflictException("Bind an Android Enterprise before deploying policies.");
        var translation = AndroidPolicyTranslator.Translate(deployment.Scenario, deployment.Settings, deployment.Apps, deployment.RevealSecret);
        var name = AndroidPolicyTranslator.PolicyName(enterprise, deployment.PolicyId);
        await api.PatchPolicyAsync(name, translation.Policy, cancellationToken);
        return new PolicyDeploymentReport(name, translation.Applied, translation.Skipped);
    }

    public async Task<CommandDispatchResult> DispatchCommandAsync(CommandDispatch dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        var device = dispatch.Device;
        var name = device.ProviderDeviceId;
        MdmCapabilities.EnsureSupported(device.Scenario, dispatch.Type);

        switch (dispatch.Type)
        {
            case DeviceCommandType.SyncInventory:
                return new CommandDispatchResult(null, Completed: true);

            case DeviceCommandType.ApplyPolicy:
                var enterprise = dispatch.EnterpriseName ?? throw new ConflictException("No Android Enterprise is bound.");
                var policyId = device.PolicyId ?? throw new ConflictException("The device has no policy assigned.");
                await api.PatchDeviceAsync(
                    name,
                    new JsonObject { ["policyName"] = AndroidPolicyTranslator.PolicyName(enterprise, policyId) },
                    "policyName",
                    cancellationToken);
                return new CommandDispatchResult(null, Completed: true);

            case DeviceCommandType.Lock:
                return new CommandDispatchResult(await api.IssueCommandAsync(name, new JsonObject { ["type"] = "LOCK" }, cancellationToken));

            case DeviceCommandType.ResetPasscode:
                return new CommandDispatchResult(await api.IssueCommandAsync(
                    name,
                    new JsonObject { ["type"] = "RESET_PASSWORD", ["resetPasswordFlags"] = new JsonArray("REQUIRE_ENTRY") },
                    cancellationToken));

            case DeviceCommandType.Reboot:
                return new CommandDispatchResult(await api.IssueCommandAsync(name, new JsonObject { ["type"] = "REBOOT" }, cancellationToken));

            case DeviceCommandType.Retire when device.Mode == ManagementMode.WorkProfile && device.Ownership == DeviceOwnership.CompanyOwned:
                // Company-owned work profile: hand the device back, keeping the personal profile.
                return new CommandDispatchResult(await api.IssueCommandAsync(name, new JsonObject { ["type"] = "RELINQUISH_OWNERSHIP" }, cancellationToken));

            case DeviceCommandType.Retire:
            case DeviceCommandType.Wipe:
                // Fully managed / dedicated: deleting the device resets it. Personally owned work profile: removes the work profile only.
                await api.DeleteDeviceAsync(name, [], dispatch.Type == DeviceCommandType.Wipe ? "Wiped by your organisation." : null, cancellationToken);
                return new CommandDispatchResult(null, Completed: true);

            default:
                throw new UnsupportedOperationException($"{dispatch.Type} is not an Android device command.");
        }
    }
}
