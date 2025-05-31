// Interfaces/ILicenseService.cs
using PortwayApi.Models.License;

namespace PortwayApi.Interfaces
{
    public interface ILicenseService
    {
        Task<LicenseInfo?> GetCurrentLicenseAsync();
        Task<bool> ActivateLicenseAsync(string licenseKey);
        Task<bool> ValidateLicenseAsync();
        Task<bool> DeactivateLicenseAsync();
        bool HasFeature(string feature);
        LicenseTier GetCurrentTier();
        bool IsProfessionalOrHigher();
    }
}