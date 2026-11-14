namespace Solvix.SharedKernel.Modules;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A vertical feature module. Each module owns its entities, EF Core
/// configurations, application services and controllers, and registers its
/// own services. The host discovers configurations and controllers through
/// <see cref="Assembly"/>.
/// </summary>
public interface IModule
{
    string Name { get; }

    Assembly Assembly { get; }

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);
}
