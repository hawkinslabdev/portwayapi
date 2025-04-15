namespace PortwayApi.Auth;

/// <summary>
/// Represents an authentication token in the system
/// </summary>
public class AuthToken
{
    public int Id { get; set; }
    public required string Username { get; set; } = $"user_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    public required string TokenHash { get; set; } = string.Empty;
    public required string TokenSalt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedAt { get; set; } = null;
    public bool IsActive => RevokedAt == null;
}