namespace PortwayApi.Models.License;

/// <summary>
/// License tier enumeration
/// </summary>
public enum LicenseTier
{
    CommunityEdition,
    Professional
}

/// <summary>
/// License helper utilities
/// </summary>
public static class LicenseHelper
{
    public static LicenseTier ParseTier(string tier)
    {
        return tier?.ToLowerInvariant() switch
        {
            "professional" or "pro" => LicenseTier.Professional,
            "community" or "free" => LicenseTier.CommunityEdition,
            _ => LicenseTier.CommunityEdition
        };
    }

    public static string GetTierDisplayName(LicenseTier tier)
    {
        return tier switch
        {
            LicenseTier.Professional => "Professional",
            LicenseTier.CommunityEdition => "Community Edition",
            _ => "Community Edition"
        };
    }

    public static bool IsProfessional(LicenseTier tier) => tier == LicenseTier.Professional;
    
    public static bool IsCommunityEdition(LicenseTier tier) => tier == LicenseTier.CommunityEdition;
}