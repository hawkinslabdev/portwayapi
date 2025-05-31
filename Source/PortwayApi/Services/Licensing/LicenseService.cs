// Services/LicenseService.cs - Fixed to match existing interface
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PortwayApi.Models.License;
using PortwayApi.Interfaces;
using Serilog;

namespace PortwayApi.Services
{
    public class LicenseService : ILicenseService
    {
        // EMBEDDED LICENSE SECRET - Same secret that Melosso server uses
        private const string EMBEDDED_LICENSE_SECRET = "mL9x2kP8vN4qR7sT1wU5yZ3aB6cE9fH2jK5nP8rU1xY4zA7bD0gJ3mQ6sV9wC2eF";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _configuration;
        private readonly string _licenseFilePath;
        private readonly string _machineId;
        private LicenseInfo? _cachedLicense;
        private DateTime _lastCheck = DateTime.MinValue;

        public LicenseService(IHttpClientFactory httpClientFactory, IConfiguration configuration)
        {
            _httpClientFactory = httpClientFactory;
            _configuration = configuration;
            _licenseFilePath = Path.Combine(Directory.GetCurrentDirectory(), ".license");
            _machineId = GetMachineId();
            
            Log.Information("🔐 License service initialized");
            _ = Task.Run(LoadLicenseAsync);
        }

        public async Task<LicenseInfo?> GetCurrentLicenseAsync()
        {
            if (_cachedLicense == null || DateTime.UtcNow - _lastCheck > TimeSpan.FromMinutes(5))
            {
                await LoadLicenseAsync();
            }
            return _cachedLicense;
        }

        public async Task<bool> ActivateLicenseAsync(string licenseKey)
        {
            try
            {
                Log.Information("🔑 Activating license: {LicenseKey}", MaskLicenseKey(licenseKey));

                var melossoCoreUrl = _configuration["License:MelossoCoreUrl"] ?? "https://melosso.com";

                using var httpClient = _httpClientFactory.CreateClient();
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var request = new
                {
                    licenseKey,
                    productId = "portway-pro",
                    machineId = _machineId,
                    version = GetVersion()
                };

                var response = await httpClient.PostAsJsonAsync($"{melossoCoreUrl}/api/licenses/activate", request);
                
                if (response.IsSuccessStatusCode)
                {
                    var responseContent = await response.Content.ReadAsStringAsync();
                    var result = JsonSerializer.Deserialize<LicenseActivationResponse>(responseContent);
                    
                    if (result?.Success == true && result.License != null)
                    {
                        // Convert from activation response to our LicenseInfo model
                        var licenseInfo = ConvertToLicenseInfo(result.License);
                        
                        // Verify signature using embedded secret
                        if (VerifyLicenseSignature(result.License))
                        {
                            await SaveLicenseAsync(licenseInfo);
                            _cachedLicense = licenseInfo;
                            Log.Information("✅ License activated and verified successfully");
                            return true;
                        }
                        else
                        {
                            Log.Error("❌ License signature verification failed");
                            return false;
                        }
                    }
                    else
                    {
                        Log.Warning("⚠️ License activation failed: {Message}", result?.Message ?? "Unknown error");
                        return false;
                    }
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    Log.Warning("⚠️ License activation request failed: {StatusCode} - {Content}", 
                        response.StatusCode, errorContent);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ License activation error");
                return false;
            }
        }

        public Task<bool> ValidateLicenseAsync()
        {
            try
            {
                if (_cachedLicense == null)
                {
                    return Task.FromResult(false);
                }

                // Check local validation first
                if (!_cachedLicense.IsValid)
                {
                    Log.Warning("⚠️ License is locally invalid (expired or inactive)");
                    return Task.FromResult(false);
                }

                // Skip remote validation if recently validated
                if (_lastCheck > DateTime.UtcNow.AddMinutes(-30))
                {
                    return Task.FromResult(true);
                }

                // For now, just rely on local validation
                // Could add remote validation here if needed
                return Task.FromResult(_cachedLicense.IsValid);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Exception during license validation");
                return Task.FromResult(_cachedLicense?.IsValid ?? false);
            }
        }

        public async Task<bool> DeactivateLicenseAsync()
        {
            try
            {
                if (File.Exists(_licenseFilePath))
                {
                    await Task.Run(() => File.Delete(_licenseFilePath));
                    Log.Information("🗑️ License file deleted");
                }

                _cachedLicense = null;
                _lastCheck = DateTime.MinValue;

                Log.Information("✅ License deactivated successfully");
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
            // Simple implementation: Professional has all features, Free has none
            return IsProfessionalOrHigher();
        }

        public LicenseTier GetCurrentTier()
        {
            if (_cachedLicense?.IsProfessional == true)
            {
                return LicenseTier.Professional;
            }
            return LicenseTier.Free;
        }

        public bool IsProfessionalOrHigher()
        {
            return _cachedLicense?.IsProfessional ?? false;
        }

        private async Task LoadLicenseAsync()
        {
            try
            {
                if (!File.Exists(_licenseFilePath))
                {
                    _cachedLicense = null;
                    return;
                }

                var content = await File.ReadAllTextAsync(_licenseFilePath);
                var licenseData = JsonSerializer.Deserialize<SignedLicenseData>(content);
                
                if (licenseData != null)
                {
                    // Verify signature using embedded secret
                    if (VerifyLicenseSignature(licenseData))
                    {
                        _cachedLicense = ConvertToLicenseInfo(licenseData);
                        _lastCheck = DateTime.UtcNow;
                        Log.Information("📋 Valid signed license loaded: {Tier}", _cachedLicense.Tier);
                    }
                    else
                    {
                        Log.Error("❌ License file signature invalid - removing tampered license");
                        File.Delete(_licenseFilePath);
                        _cachedLicense = null;
                        Log.Warning("🗑️ Deleted invalid license file - reverting to Free mode");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Failed to load license");
                _cachedLicense = null;
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
                    license.LicenseKey,
                    license.ProductId,
                    license.Status,
                    license.Tier,
                    license.MachineId,
                    license.IssuedAt?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? "",
                    license.IssuedBy ?? ""
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
                        MaskLicenseKey(license.LicenseKey));
                }

                return isValid;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error verifying license signature");
                return false;
            }
        }

        private LicenseInfo ConvertToLicenseInfo(SignedLicenseData signedData)
        {
            return new LicenseInfo
            {
                LicenseKey = signedData.LicenseKey,
                ProductId = signedData.ProductId,
                ProductName = signedData.ProductName,
                Status = signedData.Status,
                Tier = signedData.Tier,
                ExpiresAt = signedData.ExpiresAt,
                ActivatedAt = signedData.ActivatedAt,
                MachineId = signedData.MachineId,
                Features = signedData.Features ?? new List<string>()
            };
        }

        private async Task SaveLicenseAsync(LicenseInfo license)
        {
            try
            {
                // Save with signature data for verification on next load
                var signedData = new SignedLicenseData
                {
                    LicenseKey = license.LicenseKey,
                    ProductId = license.ProductId,
                    ProductName = license.ProductName,
                    Status = license.Status,
                    Tier = license.Tier,
                    ExpiresAt = license.ExpiresAt,
                    ActivatedAt = license.ActivatedAt,
                    MachineId = license.MachineId,
                    Features = license.Features,
                    IssuedBy = "melosso",
                    IssuedAt = DateTime.UtcNow,
                    Signature = "" // This should be set from the server response
                };

                var content = JsonSerializer.Serialize(signedData, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_licenseFilePath, content);
                Log.Information("💾 License saved to file");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Failed to save license file");
                throw;
            }
        }

        private string GetMachineId()
        {
            var idFile = Path.Combine(Directory.GetCurrentDirectory(), ".machine-id");
            
            try
            {
                if (File.Exists(idFile))
                {
                    return File.ReadAllText(idFile).Trim();
                }

                // Create a stable machine ID
                var machineInfo = $"{Environment.MachineName}-{Environment.UserName}-{Environment.OSVersion.Platform}";
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(machineInfo));
                var id = Convert.ToHexString(hash)[..16].ToLowerInvariant();
                
                File.WriteAllText(idFile, id);
                Log.Information("🔧 Generated machine ID: {MachineId}", id[..8] + "***");
                return id;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "⚠️ Failed to create machine ID file");
                return Environment.MachineName + "-" + Guid.NewGuid().ToString("N")[..8];
            }
        }

        private string GetVersion()
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
            return key.Length > 8 ? $"{key[..4]}***{key[^4..]}" : "***";
        }
    }

    // Helper classes for JSON serialization
    public class SignedLicenseData
    {
        public string LicenseKey { get; set; } = string.Empty;
        public string ProductId { get; set; } = string.Empty;
        public string ProductName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Tier { get; set; } = "free";
        public DateTime? ExpiresAt { get; set; }
        public DateTime? ActivatedAt { get; set; }
        public string MachineId { get; set; } = string.Empty;
        public string IssuedBy { get; set; } = string.Empty;
        public DateTime? IssuedAt { get; set; }
        public string Signature { get; set; } = string.Empty;
        public List<string> Features { get; set; } = new();
    }

    public class LicenseActivationResponse
    {
        public bool Success { get; set; }
        public SignedLicenseData? License { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}