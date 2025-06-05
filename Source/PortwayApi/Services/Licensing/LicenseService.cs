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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly string _workingDirectory;
    private readonly string _machineId;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private LicenseInfo? _cachedLicense;
    private DateTime _lastCheck = DateTime.MinValue;

    public LicenseService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _workingDirectory = Directory.GetCurrentDirectory();
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

            var jsonString = JsonSerializer.Serialize(activationRequest, _jsonOptions);
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
            
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            var response = await httpClient.PostAsync(activationUrl, content);
            
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
            await CleanupLicenseKeyFilesAsync();

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

            // Find and delete all possible license files
            var licenseFilePaths = await FindAllLicenseFilesAsync();
            
            foreach (var filePath in licenseFilePaths)
            {
                if (await TryDeleteFileAsync(filePath))
                {
                    var fileName = Path.GetFileName(filePath);
                    deletedFiles.Add(fileName);
                    Log.Debug("🗑️ Deleted license file: {FileName}", fileName);
                }
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

    public Models.License.LicenseTier GetCurrentTier()
    {
        return _cachedLicense?.IsProfessional == true 
            ? Models.License.LicenseTier.Professional 
            : Models.License.LicenseTier.CommunityEdition;
    }

    public bool IsProfessionalOrHigher()
    {
        return _cachedLicense?.IsProfessional ?? false;
    }

    #region Enhanced License File Discovery

    /// <summary>
    /// Discovers all possible license files in the working directory
    /// </summary>
    private Task<List<string>> FindAllLicenseFilesAsync()
    {
        var licenseFiles = new List<string>();
        
        try
        {
            // Get all files in the working directory
            var allFiles = Directory.GetFiles(_workingDirectory);
            
            foreach (var filePath in allFiles)
            {
                var fileName = Path.GetFileName(filePath);
                var fileNameLower = fileName.ToLowerInvariant();
                
                // Check for various license file patterns
                if (IsLicenseFile(fileName, fileNameLower))
                {
                    licenseFiles.Add(filePath);
                    Log.Debug("🔍 Found potential license file: {FileName}", fileName);
                }
            }
            
            // Sort by priority (activated licenses first, then license keys)
            licenseFiles.Sort((a, b) => GetLicenseFilePriority(a).CompareTo(GetLicenseFilePriority(b)));
            
            Log.Debug("📁 Discovered {Count} license files", licenseFiles.Count);
            
            return Task.FromResult(licenseFiles);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Error discovering license files");
            return Task.FromResult(licenseFiles);
        }
    }

    /// <summary>
    /// Determines if a file is a license file based on its name
    /// </summary>
    private static bool IsLicenseFile(string fileName, string fileNameLower)
    {
        // Original patterns (highest priority)
        if (fileName == ".license-key" || fileName == ".license")
            return true;
            
        // Files with .license extension (this covers license.license, mykey.license, etc.)
        if (fileNameLower.EndsWith(".license"))
            return true;
        
        return false;
    }

    /// <summary>
    /// Gets the priority of a license file (lower number = higher priority)
    /// </summary>
    private static int GetLicenseFilePriority(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        
        // Activated license files have highest priority
        if (fileName == ".license-key") return 1;
        
        // Original license key files
        if (fileName == ".license") return 2;
        
        // Files with .license extension (license.license, mykey.license, etc.)
        if (fileName.ToLowerInvariant().EndsWith(".license")) return 3;
        
        // Fallback (shouldn't happen with our filtering)
        return 4;
    }

    /// <summary>
    /// Attempts to load a license file and determine its type
    /// </summary>
    private async Task<(bool IsActivated, string Content)> TryLoadLicenseFileAsync(string filePath)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            
            if (string.IsNullOrWhiteSpace(content))
            {
                Log.Debug("📄 License file is empty: {FileName}", Path.GetFileName(filePath));
                return (false, string.Empty);
            }
            
            content = content.Trim();
            
            // Try to determine if this is an activated license (contains signature)
            if (content.StartsWith("{") && content.Contains("\"signature\""))
            {
                Log.Debug("🔐 Found activated license file: {FileName}", Path.GetFileName(filePath));
                return (true, content);
            }
            
            // Check if it's a downloaded license file
            if (content.StartsWith("{") && content.Contains("\"license\""))
            {
                Log.Debug("📋 Found license file with license data: {FileName}", Path.GetFileName(filePath));
                return (false, content);
            }
            
            // Assume it's a simple license key
            Log.Debug("🔑 Found simple license key file: {FileName}", Path.GetFileName(filePath));
            return (false, content);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load license file: {FileName}", Path.GetFileName(filePath));
            return (false, string.Empty);
        }
    }

    #endregion

    #region Updated License Loading Logic

    private async Task LoadLicenseAsync()
    {
        try
        {
            // Find all license files
            var licenseFiles = await FindAllLicenseFilesAsync();
            
            if (!licenseFiles.Any())
            {
                _cachedLicense = null;
                Log.Information("ℹ️ No license files found - running in Community Edition");
                return;
            }
            
            // Try to load licenses in order of priority
            foreach (var filePath in licenseFiles)
            {
                var fileName = Path.GetFileName(filePath);
                Log.Debug("🔍 Examining license file: {FileName}", fileName);
                
                var (isActivated, content) = await TryLoadLicenseFileAsync(filePath);
                
                if (string.IsNullOrEmpty(content))
                    continue;
                
                if (isActivated)
                {
                    // Try to load as activated license
                    if (await TryLoadActivatedLicenseFromContentAsync(content, fileName))
                    {
                        Log.Information("✅ Successfully loaded activated license from: {FileName}", fileName);
                        return;
                    }
                }
                else
                {
                    // Try to activate this license key
                    if (await TryLoadAndActivateLicenseFromContentAsync(content, fileName))
                    {
                        Log.Information("✅ Successfully activated license from: {FileName}", fileName);
                        return;
                    }
                }
            }
            
            // No valid license found
            _cachedLicense = null;
            Log.Warning("⚠️ Found license files but none were valid - running in Community Edition");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load license");
            _cachedLicense = null;
        }
    }

    private Task<bool> TryLoadActivatedLicenseFromContentAsync(string content, string fileName)
    {
        try
        {
            var licenseData = JsonSerializer.Deserialize<SignedLicenseData>(content, _jsonOptions);
            
            if (licenseData != null && VerifyLicenseSignature(licenseData))
            {
                _cachedLicense = ConvertToLicenseInfo(licenseData);
                _lastCheck = DateTime.UtcNow;
                Log.Information("📋 Valid activated license loaded from {FileName}: {Tier} tier", fileName, _cachedLicense.Tier);
                return Task.FromResult(true);
            }
            else
            {
                Log.Warning("❌ Invalid license signature in file: {FileName}", fileName);
                return Task.FromResult(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to parse activated license from: {FileName}", fileName);
            return Task.FromResult(false);
        }
    }

    private async Task<bool> TryLoadAndActivateLicenseFromContentAsync(string content, string fileName)
    {
        try
        {
            string licenseKey;
            
            // Check if this is a JSON license file or a simple license key
            if (content.StartsWith("{") && content.Contains("\"license\""))
            {
                try
                {
                    // This is a downloaded license file - extract the license key
                    var licenseFile = JsonSerializer.Deserialize<DownloadedLicenseFile>(content, _jsonOptions);
                    if (licenseFile?.License?.LicenseKey != null)
                    {
                        licenseKey = licenseFile.License.LicenseKey;
                        Log.Information("🔍 Extracted license key from downloaded license file: {FileName}", fileName);
                    }
                    else
                    {
                        Log.Warning("⚠️ Invalid license file format in {FileName} - missing license key", fileName);
                        return false;
                    }
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "⚠️ Failed to parse license file {FileName} as JSON", fileName);
                    return false;
                }
            }
            else
            {
                // This is a simple license key file
                licenseKey = content;
                Log.Information("🔍 Found simple license key in file: {FileName}", fileName);
            }

            Log.Information("🔍 Attempting automatic activation from: {FileName}", fileName);
            
            // Attempt to activate the license
            var activated = await ActivateLicenseAsync(licenseKey);
            
            if (!activated)
            {
                Log.Warning("⚠️ Failed to activate license from {FileName}", fileName);
                return false;
            }
            
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to load and activate license from: {FileName}", fileName);
            return false;
        }
    }

    #endregion

    #region Updated File Management

    private async Task SaveActivatedLicenseAsync(SignedLicenseData signedLicense)
    {
        try
        {
            // Save to the standard .license-key file
            var activatedLicenseFilePath = Path.Combine(_workingDirectory, ".license-key");
            
            // Redact the license key for file output only
            var redactedLicense = JsonSerializer.Deserialize<SignedLicenseData>(
                JsonSerializer.Serialize(signedLicense, _jsonOptions), _jsonOptions);
            if (redactedLicense != null)
            {
                redactedLicense.LicenseKey = MaskLicenseKeyForFile(signedLicense.LicenseKey);
            }
            var content = JsonSerializer.Serialize(redactedLicense ?? signedLicense, _jsonOptions);
            await File.WriteAllTextAsync(activatedLicenseFilePath, content, Encoding.UTF8);
            Log.Information("💾 Activated license saved to .license-key file");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to save activated license file");
            throw;
        }
    }

    private async Task CleanupLicenseKeyFilesAsync()
    {
        try
        {
            var licenseFiles = await FindAllLicenseFilesAsync();
            
            foreach (var filePath in licenseFiles)
            {
                var fileName = Path.GetFileName(filePath);
                
                // Don't delete the activated license file
                if (fileName == ".license-key")
                    continue;
                
                // Only delete license files (.license extension or exact .license filename)
                if (fileName == ".license" || fileName.ToLowerInvariant().EndsWith(".license"))
                {
                    try
                    {
                        await Task.Run(() => File.Delete(filePath));
                        Log.Information("🗑️ Removed original license file: {FileName}", fileName);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "⚠️ Failed to cleanup license file: {FileName}", fileName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Failed to cleanup license key files");
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

    #endregion

    #region Private Helper Methods

    private string GetRSAPublicKey()
    {
        const string hardcodedPublicKey = @"-----BEGIN PUBLIC KEY-----
MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEApE+cOD4v1xLesrmVsE54
H6oHSFNEE3WKxc3GSRLnzSuiHt79I5JPuS44YXs8gwFmltL615mfqyY1FL9VmmM1
raQBhTEu94Mh4CbLvo2YWABdNpvb9Mka1DflBCdj2OoSh02RPBuHB/uMTuW60qPD
UTBuevA/ZE44Pz+k1eMIOt6S1AkbMkidpJQQjBoe5LQIwqYzCQYSI6QihqNff+xH
453VZnOGlv1zWcNQSLQLT0d5xafldUe4H3mEyayXIxVcD87aZa4pc2mFUFbct+AQ
OM04QdYoLsjAtO33495iZdv5gQV3QJG6GSNd5DxKdG2u5frKDAWbO2CFbrRlx2JH
AwIDAQAB
-----END PUBLIC KEY-----";
        
        return hardcodedPublicKey;
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

            // Verify RSA signature using public key from configuration
            using var rsa = RSA.Create();
            rsa.ImportFromPem(GetRSAPublicKey());

            var dataBytes = Encoding.UTF8.GetBytes(dataToVerify);
            var signatureBytes = Convert.FromBase64String(license.Signature);

            var isValid = rsa.VerifyData(
                dataBytes,
                signatureBytes,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss
            );

            if (!isValid)
            {
                Log.Warning("❌ RSA signature verification failed for key: {LicenseKey}",
                    MaskLicenseKey(license.LicenseKey ?? "unknown"));
            }

            return isValid;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error verifying RSA signature: {Error}", ex.Message);
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
            Tier = signedData.Tier ?? "community",
            ExpiresAt = signedData.ExpiresAt,
            ActivatedAt = signedData.ActivatedAt,
            MachineId = signedData.MachineId ?? string.Empty,
            Features = signedData.Features ?? new List<string>()
        };
    }

    private string GetMachineId()
    {
        var idFile = Path.Combine(_workingDirectory, ".machine-id");
        
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

    private static string MaskLicenseKeyForFile(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "***";
        if (key.Length <= 8)
            return new string('*', key.Length);
        return $"{key[..4]}***{key[^4..]}";
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
    public string Tier { get; set; } = "community";

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

public class DownloadedLicenseFile
{
    [JsonPropertyName("fileVersion")]
    public string FileVersion { get; set; } = string.Empty;

    [JsonPropertyName("license")]
    public SignedLicenseData? License { get; set; }

    [JsonPropertyName("instructions")]
    public object? Instructions { get; set; }
}

#endregion