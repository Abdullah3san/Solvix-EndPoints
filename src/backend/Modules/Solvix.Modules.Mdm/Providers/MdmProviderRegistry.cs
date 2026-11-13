namespace Solvix.Modules.Mdm.Providers;

using Solvix.Modules.Mdm.Domain;
using Solvix.SharedKernel.Exceptions;

public interface IMdmProviderRegistry
{
    /// <summary>Returns the provider or throws <see cref="UnsupportedOperationException"/> when it is missing or unconfigured.</summary>
    IMdmProvider For(MobilePlatform platform);

    bool IsConfigured(MobilePlatform platform);
}

internal sealed class MdmProviderRegistry(IEnumerable<IMdmProvider> providers) : IMdmProviderRegistry
{
    private readonly Dictionary<MobilePlatform, IMdmProvider> _providers = providers.ToDictionary(p => p.Platform);

    public bool IsConfigured(MobilePlatform platform) => _providers.TryGetValue(platform, out var p) && p.IsConfigured;

    public IMdmProvider For(MobilePlatform platform)
    {
        if (!_providers.TryGetValue(platform, out var provider))
        {
            throw new UnsupportedOperationException($"No management provider is registered for {platform}.");
        }

        if (!provider.IsConfigured)
        {
            throw new UnsupportedOperationException(
                $"{platform} management is not configured on this server. See docs/PHASE-08-MOBILE-DEVICE-MANAGEMENT.md.");
        }

        return provider;
    }
}
