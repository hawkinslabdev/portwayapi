using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PortwayApi.Interfaces;
using Serilog;

namespace PortwayApi.Services;

/// <summary>
/// Hosted service to ensure license is loaded and validated on application startup
/// </summary>
public class LicenseStartupService : IHostedService
{
    private readonly ILicenseService _licenseService;
    private readonly ILogger<LicenseStartupService> _logger;

    public LicenseStartupService(ILicenseService licenseService, ILogger<LicenseStartupService> logger)
    {
        _licenseService = licenseService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("🔐 License service starting...");
            
            // This will trigger the license loading/activation process
            var currentLicense = await _licenseService.GetCurrentLicenseAsync();
            
            if (currentLicense != null)
            {
                _logger.LogInformation("✅ License loaded successfully - Tier: {Tier}, Status: {Status}", 
                    currentLicense.Tier, currentLicense.Status);
                
                if (currentLicense.IsProfessional)
                {
                    _logger.LogInformation("🌟 Portway Professional features enabled");
                    
                    if (currentLicense.ExpiresAt.HasValue)
                    {
                        _logger.LogInformation("📅 License expires: {ExpiryDate}", 
                            currentLicense.ExpiresAt.Value.ToString("yyyy-MM-dd"));
                    }
                    else
                    {
                        _logger.LogInformation("🔄 License: Perpetual (no expiration)");
                    }
                }
            }
            else
            {
                _logger.LogInformation("ℹ️ No valid license found - running in Free mode");
                
                // Check if there's a license key file that failed activation
                var licenseKeyPath = Path.Combine(Directory.GetCurrentDirectory(), ".license");
                if (File.Exists(licenseKeyPath))
                {
                    _logger.LogWarning("⚠️ Found .license file but activation failed - check license key validity");
                }
                else
                {
                    _logger.LogInformation("💡 To activate Professional features, create a .license file with your license key");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Error during license service startup");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}