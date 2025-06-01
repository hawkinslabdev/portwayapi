// Services/Licensing/Extensions/LicenseServiceExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using PortwayApi.Interfaces;
using PortwayApi.Services;

namespace PortwayApi.Services;

/// <summary>
/// Extension methods for registering license services
/// </summary>
public static class LicenseServiceExtensions
{
    /// <summary>
    /// Adds license services to the service collection
    /// </summary>
    public static IServiceCollection AddLicenseServices(this IServiceCollection services)
    {
        // Register the license service
        services.AddSingleton<ILicenseService, LicenseService>();
        
        // Register the license manager as both singleton and hosted service
        services.AddSingleton<LicenseManager>();
        services.AddHostedService<LicenseManager>(provider => provider.GetRequiredService<LicenseManager>());
        
        return services;
    }
}