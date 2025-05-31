// Models/License/LicenseInfo.cs
using System.Text.Json.Serialization;

namespace PortwayApi.Models.License;

public class LicenseInfo
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

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new();

    public bool IsValid => Status == "active" && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);
    public bool IsProfessional => Tier == "professional" && IsValid;
}

public enum LicenseTier
{
    Free,
    Professional
}

public static class LicenseHelper
{
    public static LicenseTier ParseTier(string tier)
    {
        return tier?.ToLowerInvariant() switch
        {
            "professional" or "pro" => LicenseTier.Professional,
            _ => LicenseTier.Free
        };
    }

    public static string GetTierDisplayName(LicenseTier tier)
    {
        return tier switch
        {
            LicenseTier.Professional => "Professional",
            _ => "Free"
        };
    }

    public static bool IsProfessional(LicenseTier tier) => tier == LicenseTier.Professional;
}
