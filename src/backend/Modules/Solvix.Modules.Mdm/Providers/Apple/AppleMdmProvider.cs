namespace Solvix.Modules.Mdm.Providers.Apple;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// Apple MDM provider. Unlike Android there is no cloud API: Solvix <em>is</em> the MDM server.
/// This class creates the enrollment artefacts, validates policy translations at publish time and wakes devices via
/// APNs. The protocol itself (check-in and command exchange) is handled by <c>AppleDeviceProtocolService</c>.
/// </summary>
public sealed class AppleMdmProvider(IApplePushClient push, IOptions<MdmOptions> options, ILogger<AppleMdmProvider> logger) : IMdmProvider
{
    public MobilePlatform Platform => MobilePlatform.Ios;

    public bool IsConfigured => options.Value.Apple.IsConfigured && !string.IsNullOrWhiteSpace(options.Value.Apple.ScepUrl);

    public Task<EnrollmentArtifact> CreateEnrollmentAsync(EnrollmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var secret = request.Secret ?? throw new InvalidOperationException("An Apple enrollment requires a secret.");
        var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
        var scenario = request.Scenario;
        var instructions = scenario.Mode switch
        {
            ManagementMode.UserEnrollment => new[]
            {
                "Send the link to the user. They open it on their iPhone or iPad, then install the profile from Settings > Profile Downloaded.",
                "The user needs a Managed Apple ID. Only work apps and work data are managed; the device itself and personal data are not.",
            },
            ManagementMode.AutomatedDeviceEnrollment => new[]
            {
                "Assign the device to this MDM server in Apple Business Manager; it enrolls during Setup Assistant.",
                "The enrollment link can also be used to enroll a supervised-capable device manually.",
            },
            _ => new[]
            {
                "Open the link in Safari on the device and install the profile from Settings > Profile Downloaded.",
                "The device is managed but not supervised unless it was enrolled through Apple Business Manager.",
            },
        };

        return Task.FromResult(new EnrollmentArtifact
        {
            EnrollmentUrl = $"{baseUrl}/api/v1/mdm/apple/enroll?token={Uri.EscapeDataString(secret)}",
            Instructions = instructions,
        });
    }

    public Task<PolicyDeploymentReport> DeployPolicyAsync(PolicyDeployment deployment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        // Translating now surfaces invalid policies when they are published rather than when a device fetches them.
        var translation = AppleProfileBuilder.BuildPolicyProfile(
            deployment.PolicyId,
            deployment.Name,
            deployment.Version,
            deployment.Scenario,
            deployment.Settings,
            deployment.Apps,
            deployment.RevealSecret);
        return Task.FromResult(new PolicyDeploymentReport(translation.Identifier, translation.Applied, translation.Skipped));
    }

    public async Task<CommandDispatchResult> DispatchCommandAsync(CommandDispatch dispatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        MdmCapabilities.EnsureSupported(dispatch.Device.Scenario, dispatch.Type);

        var device = dispatch.Device;
        if (string.IsNullOrEmpty(device.PushToken) || string.IsNullOrEmpty(device.PushMagic))
        {
            // The command stays queued; the device receives it the next time it checks in.
            return new CommandDispatchResult(null, StaysQueued: true);
        }

        try
        {
            await push.SendMdmPushAsync(device.PushToken, device.PushMagic, device.PushTopic ?? options.Value.Apple.PushTopic!, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // Not fatal: the command is queued and the sync worker pushes again.
            logger.LogWarning(ex, "APNs push failed for command {CommandId}; it stays queued", dispatch.CommandId);
        }

        return new CommandDispatchResult(null, StaysQueued: true);
    }
}
