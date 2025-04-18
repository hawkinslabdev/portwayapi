namespace PortwayApi.Auth;

/// <summary>
/// Represents an authentication token in the system with enhanced security features
/// </summary>
public class AuthToken
{
    public int Id { get; set; }
    public required string Username { get; set; } = $"user_{Guid.NewGuid().ToString("N").Substring(0, 8)}";
    public required string TokenHash { get; set; } = string.Empty;
    public required string TokenSalt { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; } = null;
    public DateTime? RevokedAt { get; set; } = null;
    
    /// <summary>
    /// Comma-separated list of allowed endpoint scopes (e.g., "Products,Customers,*")
    /// Use "*" for full access to all endpoints
    /// </summary>
    public string AllowedScopes { get; set; } = "*";
    
    /// <summary>
    /// Token description for administrative purposes
    /// </summary>
    public string Description { get; set; } = string.Empty;
    
    /// <summary>
    /// Indicates if token is currently active
    /// </summary>
    public bool IsActive => RevokedAt == null && (ExpiresAt == null || ExpiresAt > DateTime.UtcNow);
    
    /// <summary>
    /// Parses allowed scopes into a list
    /// </summary>
    public List<string> GetScopesList()
    {
        if (string.IsNullOrWhiteSpace(AllowedScopes))
            return new List<string>();
            
        return AllowedScopes.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }
    
    /// <summary>
    /// Checks if token has access to specified endpoint
    /// </summary>
    public bool HasAccessToEndpoint(string endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
            return false;
            
        // Universal access
        if (AllowedScopes == "*")
            return true;
            
        var scopes = GetScopesList();
        
        // Check for direct matches (case-insensitive)
        if (scopes.Any(s => s.Equals("*") || s.Equals(endpointName, StringComparison.OrdinalIgnoreCase)))
            return true;
            
        // Check for wildcard matches (e.g., "Products*" should match "ProductsDetails")
        return scopes.Any(s => 
            s.EndsWith("*") && 
            endpointName.StartsWith(s.Substring(0, s.Length - 1), StringComparison.OrdinalIgnoreCase));
    }
}