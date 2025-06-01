using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PortwayApi.Models.License;
using PortwayApi.Interfaces;
using Serilog;

namespace PortwayApi.Services;

public class LicenseService : ILicenseService
{
    private const string EMBEDDED_LICENSE_SECRET = "2WVXTcztRoRGSCiyMc3O5y+Yaym16ChiqwE8i9jviQCsy28mYU52PLTVn2HIt+jjKBLEphKK96amWWgBGcXr1A==";
    
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly string _licenseKeyFilePath;
    private readonly string _activatedLicenseFilePath;
    private readonly string _machineId;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private LicenseInfo? _cachedLicense;
    private DateTime _lastCheck = DateTime.MinValue;

    public LicenseService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _licenseKeyFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".license");
        _activatedLicenseFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".license-key");
        _machineId = GetMachineId();
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        
        Log.Information("🔐 License service initialized with machine ID: {MachineId}", MaskString(_machineId, 6));
    }

    public async Task<LicenseInfo?> GetCurrentLicenseAsync()
    {
        if (_cachedLicense == null)
        {
            await LoadLicenseAsync();
        }
        return _cachedLicense;
    }

    public async Task<bool> ActivateLicenseAsync(string licenseKey)
    {
        try
        {
            // Validate input
            if (string.IsNullOrWhiteSpace(licenseKey))
            {
                Log.Warning("❌ License key is null or empty");
                return false;
            }

            licenseKey = licenseKey.Trim();

            Log.Information("🔑 Attempting to activate license: {LicenseKey}", MaskLicenseKey(licenseKey));

            var melossoCoreUrl = _configuration["License:MelossoCoreUrl"] ?? "https://melosso.com";
            var activationUrl = $"{melossoCoreUrl}/api/licenses/activate";

            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            
            // Add proper headers
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Portway/1.0");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/json");

            // Create activation request with exact field names expected by server
            var activationRequest = new LicenseActivationRequest
            {
                LicenseKey = licenseKey,
                ProductId = "portway-pro",
                MachineId = _machineId,
                Version = GetVersion()
            };

            Log.Debug("📤 Sending activation request to: {Url}", activationUrl);
            Log.Debug("📋 Request payload: ProductId={ProductId}, MachineId={MachineId}, Version={Version}", 
                activationRequest.ProductId, MaskString(_machineId, 6), activationRequest.Version);

            var response = await httpClient.PostAsJsonAsync(activationUrl, activationRequest, _jsonOptions);
            
            var responseContent = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Log.Warning("⚠️ License activation failed: {StatusCode} - {Content}", 
                    response.StatusCode, responseContent);
                return false;
            }

            var result = JsonSerializer.Deserialize<LicenseActivationResponse>(responseContent, _jsonOptions);
            
            if (result?.Success != true)
            {
                Log.Warning("⚠️ License activation unsuccessful: {Message}", result?.Message ?? "Unknown error");
                return false;
            }

            if (result.License == null)
            {
                Log.Error("❌ License activation response missing license data");
                return false;
            }

            // Verify signature using embedded secret
            if (!VerifyLicenseSignature(result.License))
            {
                Log.Error("❌ License signature verification failed");
                return false;
            }

            // Convert and save the activated license
            var licenseInfo = ConvertToLicenseInfo(result.License);
            await SaveActivatedLicenseAsync(result.License);
            
            // Delete the original license key file since we no longer need it
            await CleanupLicenseKeyFileAsync();

            _cachedLicense = licenseInfo;
            _lastCheck = DateTime.UtcNow;
            
            Log.Information("✅ License activated successfully: {Tier} tier, expires: {Expires}", 
                licenseInfo.Tier, licenseInfo.ExpiresAt?.ToString("yyyy-MM-dd") ?? "Never");
            
            return true;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "❌ Network error during license activation");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            Log.Error(ex, "❌ License activation timed out");
            return false;
        }
        catch (JsonException ex)
        {
            Log.Error(ex, "❌ Invalid JSON response during license activation");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unexpected error during license activation");
            return false;
        }
    }

    public Task<bool> ValidateLicenseAsync()
    {
        try
        {
            if (_cachedLicense == null)
            {
                Log.Debug("📋 No license cached - validation failed");
                return Task.FromResult(false);
            }

            // Only local validation - never check server again
            var isValid = _cachedLicense.IsValid;
            
            if (!isValid)
            {
                if (_cachedLicense.ExpiresAt.HasValue && _cachedLicense.ExpiresAt < DateTime.UtcNow)
                {
                    Log.Warning("⚠️ License has expired: {ExpirationDate}", _cachedLicense.ExpiresAt);
                }
                else
                {
                    Log.Warning("⚠️ License is invalid - status: {Status}", _cachedLicense.Status);
                }
            }
            else
            {
                Log.Debug("✅ License validation successful: {Tier} tier", _cachedLicense.Tier);
            }

            return Task.FromResult(isValid);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Exception during license validation");
            return Task.FromResult(false);
        }
    }

    public async Task<bool> DeactivateLicenseAsync()
    {
        try
        {
            var deletedFiles = new List<string>();

            // Delete activated license file
            if (await TryDeleteFileAsync(_activatedLicenseFilePath))
            {
                deletedFiles.Add("activated license");
            }

            // Delete license key file
            if (await TryDeleteFileAsync(_licenseKeyFilePath))
            {
                deletedFiles.Add("license key");
            }

            // Clear cached license
            _cachedLicense = null;
            _lastCheck = DateTime.MinValue;

            if (deletedFiles.Any())
            {
                Log.Information("✅ License deactivated successfully - removed: {Files}", 
                    string.Join(", ", deletedFiles));
                Log.Information("💡 You can now activate a new license key");
            }
            else
            {
                Log.Information("ℹ️ No license files found to delete");
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to deactivate license");
            return false;
        }
    }

    public bool HasFeature(string feature)
    {
        if (string.IsNullOrWhiteSpace(feature))
            return false;
            
        var currentLicense = _cachedLicense;
        if (currentLicense?.IsProfessional != true)
            return false;
            
        // For now, Professional tier has all features
        return true;
    }

    public LicenseTier GetCurrentTier()
    {
        return _cachedLicense?.IsProfessional == true ? LicenseTier.Professional : LicenseTier.Free;
    }

    public bool IsProfessionalOrHigher()
    {
        return _cachedLicense?.IsProfessional ?? false;
    }

    #region Private Methods

    private async Task LoadLicenseAsync()
    {
        try
        {
            // First priority: Check for activated license file
            if (File.Exists(_activatedLicenseFilePath))
            {
                if (await TryLoadActivatedLicenseAsync())
                {
                    return;
                }
            }

            // Second priority: Check for license key file that needs activation
            if (File.Exists(_licenseKeyFilePath))
            {
                await TryLoadAndActivateLicenseKeyAsync();
                return;
            }

            // No license found
            _cachedLicense = null;
            Log.Information("ℹ️ No license found - running in Free mode");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load license");
            _cachedLicense = null;
        }
    }

    private async Task<bool> TryLoadActivatedLicenseAsync()
    {
        try
        {
            var content = await File.ReadAllTextAsync(_activatedLicenseFilePath, Encoding.UTF8);
            var licenseData = JsonSerializer.Deserialize<SignedLicenseData>(content, _jsonOptions);
            
            if (licenseData != null && VerifyLicenseSignature(licenseData))
            {
                _cachedLicense = ConvertToLicenseInfo(licenseData);
                _lastCheck = DateTime.UtcNow;
                Log.Information("📋 Valid activated license loaded: {Tier} tier", _cachedLicense.Tier);
                return true;
            }
            else
            {
                Log.Error("❌ Activated license file signature invalid - removing tampered license");
                await TryDeleteFileAsync(_activatedLicenseFilePath);
                Log.Warning("🗑️ Deleted invalid activated license file");
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load activated license file");
            return false;
        }
    }

    private async Task TryLoadAndActivateLicenseKeyAsync()
    {
        try
        {
            var licenseKey = (await File.ReadAllTextAsync(_licenseKeyFilePath, Encoding.UTF8)).Trim();
            
            if (string.IsNullOrEmpty(licenseKey))
            {
                Log.Warning("⚠️ License key file is empty");
                return;
            }

            Log.Information("🔍 Found license key file - attempting automatic activation");
            
            // Attempt to activate the license
            var activated = await ActivateLicenseAsync(licenseKey);
            
            if (!activated)
            {
                Log.Warning("⚠️ Failed to activate license from .license file");
                // Keep the file so user can try again or fix issues
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load and activate license key file");
        }
    }

    private bool VerifyLicenseSignature(SignedLicenseData license)
    {
        try
        {
            if (string.IsNullOrEmpty(license.Signature))
            {
                Log.Warning("⚠️ License missing signature");
                return false;
            }

            // Recreate the exact same string that was signed on server
            var dataToVerify = string.Join("|", new[]
            {
                license.LicenseKey ?? string.Empty,
                license.ProductId ?? string.Empty,
                license.Status ?? string.Empty,
                license.Tier ?? string.Empty,
                license.MachineId ?? string.Empty,
                license.IssuedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? string.Empty,
                license.IssuedBy ?? string.Empty
            });

            // Calculate expected HMAC signature using embedded secret
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(EMBEDDED_LICENSE_SECRET));
            var dataBytes = Encoding.UTF8.GetBytes(dataToVerify);
            var expectedSignatureBytes = hmac.ComputeHash(dataBytes);

            // Compare signatures (timing-safe)
            var providedSignatureBytes = Convert.FromBase64String(license.Signature);
            var isValid = CryptographicOperations.FixedTimeEquals(
                expectedSignatureBytes,
                providedSignatureBytes
            );

            if (!isValid)
            {
                Log.Warning("❌ License signature verification failed for key: {LicenseKey}", 
                    MaskLicenseKey(license.LicenseKey ?? "unknown"));
            }

            return isValid;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error verifying license signature");
            return false;
        }
    }

    private static LicenseInfo ConvertToLicenseInfo(SignedLicenseData signedData)
    {
        return new LicenseInfo
        {
            LicenseKey = signedData.LicenseKey ?? string.Empty,
            ProductId = signedData.ProductId ?? string.Empty,
            ProductName = signedData.ProductName ?? string.Empty,
            Status = signedData.Status ?? string.Empty,
            Tier = signedData.Tier ?? "free",
            ExpiresAt = signedData.ExpiresAt,
            ActivatedAt = signedData.ActivatedAt,
            MachineId = signedData.MachineId ?? string.Empty,
            Features = signedData.Features ?? new List<string>()
        };
    }

    private async Task SaveActivatedLicenseAsync(SignedLicenseData signedLicense)
    {
        try
        {
            var content = JsonSerializer.Serialize(signedLicense, _jsonOptions);
            await File.WriteAllTextAsync(_activatedLicenseFilePath, content, Encoding.UTF8);
            Log.Information("💾 Activated license saved to .license-key file");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to save activated license file");
            throw;
        }
    }

    private async Task CleanupLicenseKeyFileAsync()
    {
        try
        {
            if (File.Exists(_licenseKeyFilePath))
            {
                await Task.Run(() => File.Delete(_licenseKeyFilePath));
                Log.Information("🗑️ Removed original license key file");
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to cleanup license key file");
        }
    }

    private async Task<bool> TryDeleteFileAsync(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                await Task.Run(() => File.Delete(filePath));
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to delete file: {FilePath}", filePath);
            return false;
        }
    }

    private string GetMachineId()
    {
        var idFile = Path.Combine(Directory.GetCurrentDirectory(), ".machine-id");
        
        try
        {
            if (File.Exists(idFile))
            {
                var fileId = File.ReadAllText(idFile, Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(fileId))
                {
                    return fileId;
                }
            }

            // Create a stable machine ID
            var machineInfo = $"{Environment.MachineName}-{Environment.UserName}-{Environment.OSVersion.Platform}";
            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineInfo));
            var generatedId = Convert.ToHexString(hash)[..16].ToLowerInvariant();
            
            File.WriteAllText(idFile, generatedId, Encoding.UTF8);
            Log.Information("🔧 Generated machine ID: {MachineId}", MaskString(generatedId, 6));
            return generatedId;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to create machine ID file");
            return Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
        }
    }

    private static string GetVersion()
    {
        try
        {
            return System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        }
        catch
        {
            return "1.0.0";
        }
    }

    private static string MaskLicenseKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "***";
            
        return key.Length > 8 ? $"{key[..4]}***{key[^4..]}" : "***";
    }

    private static string MaskString(string value, int visibleChars)
    {
        if (string.IsNullOrEmpty(value))
            return "***";
            
        if (value.Length <= visibleChars)
            return new string('*', value.Length);
            
        return $"{value[..visibleChars]}***";
    }

    #endregion
}

#region Data Transfer Objects

public class LicenseActivationRequest
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; set; } = string.Empty;

    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;
}

public class SignedLicenseData
{
    [JsonPropertyName("licenseKey")]
    public string LicenseKey { get; set; } = string.Empty;

    [JsonPropertyName("productId")]
    public string ProductId { get; set; } = string.Empty;

    [JsonPropertyName("productName")]
    public string ProductName { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("tier")]
    public string Tier { get; set; } = "free";

    [JsonPropertyName("expiresAt")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("activatedAt")]
    public DateTime? ActivatedAt { get; set; }

    [JsonPropertyName("machineId")]
    public string MachineId { get; set; } = string.Empty;

    [JsonPropertyName("issuedBy")]
    public string IssuedBy { get; set; } = string.Empty;

    [JsonPropertyName("issuedAt")]
    public DateTime? IssuedAt { get; set; }

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new();
}

public class LicenseActivationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("license")]
    public SignedLicenseData? License { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

#endregion