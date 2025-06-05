using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PortwayApi.Interfaces;
using PortwayApi.Models.License;
using Serilog;

namespace PortwayApi.Services;

/// <summary>
/// Singleton service that manages license state and performs startup validation
/// </summary>
public class LicenseManager : IHostedService, IDisposable
{
    private readonly ILicenseService _licenseService;
    private readonly ILogger<LicenseManager> _logger;
    private readonly Timer? _periodicCheckTimer;
    private LicenseInfo? _cachedLicense;
    private LicenseTier _currentTier = LicenseTier.CommunityEdition;
    private DateTime _lastCheck = DateTime.MinValue;
    private readonly object _lockObject = new object();

    public LicenseManager(ILicenseService licenseService, ILogger<LicenseManager> logger)
    {
        _licenseService = licenseService;
        _logger = logger;
        
        // Check license every 6 hours for expiration/revocation
        _periodicCheckTimer = new Timer(
            async _ => await PerformPeriodicCheckAsync(),
            null,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(6)
        );
    }

    /// <summary>
    /// Gets the current license tier (cached, no async calls)
    /// </summary>
    public LicenseTier CurrentTier
    {
        get
        {
            lock (_lockObject)
            {
                return _currentTier;
            }
        }
    }

    /// <summary>
    /// Gets the current license edition name
    /// </summary>
    public string CurrentEdition => LicenseHelper.GetTierDisplayName(CurrentTier);

    /// <summary>
    /// Checks if the current license is Professional or higher (cached, no async calls)
    /// </summary>
    public bool IsProfessionalOrHigher
    {
        get
        {
            lock (_lockObject)
            {
                return LicenseHelper.IsProfessional(_currentTier);
            }
        }
    }

    /// <summary>
    /// Checks if a specific feature is available (cached, no async calls)
    /// </summary>
    public bool HasFeature(string feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
            return false;
            
        lock (_lockObject)
        {
            // Professional tier has all features
            return LicenseHelper.IsProfessional(_currentTier);
        }
    }

    /// <summary>
    /// Gets the cached license information (may be null for Community Edition)
    /// </summary>
    public LicenseInfo? CachedLicense
    {
        get
        {
            lock (_lockObject)
            {
                return _cachedLicense;
            }
        }
    }

    /// <summary>
    /// Gets license status for display purposes
    /// </summary>
    public string LicenseStatus
    {
        get
        {
            lock (_lockObject)
            {
                if (_cachedLicense?.IsValid == true)
                {
                    return _cachedLicense.ExpiresAt.HasValue 
                        ? $"Licensed until {_cachedLicense.ExpiresAt.Value:yyyy-MM-dd}"
                        : "Licensed (Perpetual)";
                }
                return "Community Edition";
            }
        }
    }

    /// <summary>
    /// Refreshes the license cache (should only be called when license files change)
    /// </summary>
    public async Task RefreshLicenseAsync()
    {
        try
        {
            Log.Information("🔄 Refreshing license cache...");
            await LoadAndCacheLicenseAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error refreshing license cache");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            Log.Information("🔐 License Manager starting...");
            await LoadAndCacheLicenseAsync();
            Log.Information("✅ License Manager started successfully");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during License Manager startup");
            // Don't fail startup - default to Community Edition
            lock (_lockObject)
            {
                _currentTier = LicenseTier.CommunityEdition;
                _cachedLicense = null;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Log.Information("🛑 License Manager stopping...");
        _periodicCheckTimer?.Change(Timeout.Infinite, 0);
        return Task.CompletedTask;
    }

    private async Task LoadAndCacheLicenseAsync()
    {
        try
        {
            var license = await _licenseService.GetCurrentLicenseAsync();
            
            lock (_lockObject)
            {
                _cachedLicense = license;
                _lastCheck = DateTime.UtcNow;
                
                if (license?.IsValid == true && license.IsProfessional)
                {
                    _currentTier = LicenseTier.Professional;
                    
                    Log.Information("✅ Professional license loaded - Tier: {Tier}, Status: {Status}", 
                        license.Tier, license.Status);
                    
                    if (license.ExpiresAt.HasValue)
                    {
                        Log.Information("📅 License expires: {ExpiryDate}", 
                            license.ExpiresAt.Value.ToString("yyyy-MM-dd"));
                    }
                    else
                    {
                        Log.Information("🔄 License: Perpetual (no expiration)");
                    }
                    
                    Log.Information("🌟 Portway Professional features enabled");
                }
                else
                {
                    _currentTier = LicenseTier.CommunityEdition;
                    
                    if (license != null && !license.IsValid)
                    {
                        Log.Warning("⚠️ Invalid license found - running in Community Edition");
                        
                        if (license.ExpiresAt.HasValue && license.ExpiresAt < DateTime.UtcNow)
                        {
                            Log.Warning("⏰ License has expired: {ExpirationDate}", license.ExpiresAt.Value);
                        }
                    }
                    else
                    {
                        Log.Information("ℹ️ No valid license found - running in Community Edition");
                        
                        // Check if there's a license key file that failed activation
                        var licenseKeyPath = Path.Combine(Directory.GetCurrentDirectory(), ".license");
                        if (File.Exists(licenseKeyPath))
                        {
                            Log.Warning("⚠️ Found .license file but activation failed - check license key validity");
                        }
                        else
                        {
                            Log.Information("💡 To activate Professional features, create a .license file with your license key");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error loading license");
            
            lock (_lockObject)
            {
                _currentTier = LicenseTier.CommunityEdition;
                _cachedLicense = null;
                _lastCheck = DateTime.UtcNow;
            }
        }
    }

    private async Task PerformPeriodicCheckAsync()
    {
        try
        {
            // Only check if we have a Professional license that might expire
            lock (_lockObject)
            {
                if (_currentTier != LicenseTier.Professional || 
                    _cachedLicense?.ExpiresAt == null ||
                    DateTime.UtcNow - _lastCheck < TimeSpan.FromHours(6))
                {
                    return;
                }
            }

            Log.Debug("🔍 Performing periodic license validation...");
            
            var isValid = await _licenseService.ValidateLicenseAsync();
            
            lock (_lockObject)
            {
                if (!isValid && _currentTier == LicenseTier.Professional)
                {
                    Log.Warning("⚠️ Professional license is no longer valid - switching to Community Edition");
                    _currentTier = LicenseTier.CommunityEdition;
                    _cachedLicense = null;
                }
                _lastCheck = DateTime.UtcNow;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Error during periodic license check");
        }
    }

    public void Dispose()
    {
        _periodicCheckTimer?.Dispose();
    }
}