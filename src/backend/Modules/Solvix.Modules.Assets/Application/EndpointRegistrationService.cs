namespace Solvix.Modules.Assets.Application;

using Solvix.Modules.Assets.Domain;
using Solvix.SharedKernel.Endpoints;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Identity;

public interface IEndpointRegistrationService
{
    /// <summary>Creates an asset record for a managed endpoint that has none, pre-filled from its inventory, and links the two.</summary>
    Task<AssetDetailsDto> RegisterAsync(RegisterEndpointRequest request, CancellationToken cancellationToken);
}

internal sealed class EndpointRegistrationService(
    IAssetService assets,
    IAssetMatchingService matching,
    IEndpointDirectory endpoints,
    IMemberDirectory members) : IEndpointRegistrationService
{
    public async Task<AssetDetailsDto> RegisterAsync(RegisterEndpointRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = await endpoints.FindAsync([request.DeviceId], cancellationToken);
        if (!found.TryGetValue(request.DeviceId, out var device))
        {
            throw new NotFoundException("Device", request.DeviceId);
        }

        var existing = await matching.GetForDeviceAsync(device.Id, cancellationToken);
        if (existing.Approved is not null)
        {
            throw new ConflictException($"The endpoint is already associated with asset {existing.Approved.Asset.AssetTag}.", "device_already_linked");
        }

        // Carry the endpoint's primary user over as the holder when they are still an active member.
        AssignAssetRequest? assignTo = null;
        if (device.AssignedUserId is { } userId && (await members.FindAsync([userId], cancellationToken)).TryGetValue(userId, out var member) && member.IsActive)
        {
            assignTo = new AssignAssetRequest(userId, null, null, null, "Primary user of the managed endpoint.");
        }

        var created = await assets.CreateAsync(
            new CreateAssetRequest
            {
                Asset = new AssetInput
                {
                    AssetTag = request.AssetTag,
                    Name = string.IsNullOrWhiteSpace(request.Name) ? device.DisplayName ?? device.Hostname : request.Name,
                    Category = request.Category,
                    Condition = AssetCondition.Good,
                    Manufacturer = device.Manufacturer,
                    Model = device.Model,
                    SerialNumber = device.SerialNumber,
                    BranchId = device.BranchId,
                    DepartmentId = device.DepartmentId,
                    Description = $"Registered from managed endpoint {device.Hostname}.",
                },
                AssignTo = assignTo,
            },
            cancellationToken);

        // Creating the asset already produced a suggestion when the serial number is usable; approve it explicitly.
        await matching.LinkManuallyAsync(created.Summary.Id, new ManualLinkRequest(device.Id, "Asset registered from this endpoint."), cancellationToken);
        return await assets.GetAsync(created.Summary.Id, cancellationToken);
    }
}
