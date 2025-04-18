namespace PortwayApi.Auth;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Serilog;

public class TokenService
{
    private readonly AuthDbContext _dbContext;
    private readonly string _tokenFolderPath;
    
    public TokenService(AuthDbContext dbContext)
    {
        _dbContext = dbContext;
        _tokenFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "tokens");
        
        // Ensure tokens directory exists
        if (!Directory.Exists(_tokenFolderPath))
        {
            Directory.CreateDirectory(_tokenFolderPath);
            Log.Debug("✅ Created tokens directory: {Directory}", _tokenFolderPath);
        }
    }
    
    // Generate a new token for a user
    public async Task<string> GenerateTokenAsync(string username)
    {
        // Generate a random token
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
            .Replace("+", "-") // Replace '+' with URL-safe '-'
            .Replace("/", "_") // Replace '/' with URL-safe '_'
            .TrimEnd('=');     // Remove padding '=' for URL safety       
             
        // Generate salt for hashing
        byte[] salt = GenerateSalt();
        string saltString = Convert.ToBase64String(salt);
        
        // Hash the token
        string hashedToken = HashToken(token, salt);
        
        // Create a new token entry
        var tokenEntry = new AuthToken
        {
            Username = username,
            TokenHash = hashedToken,
            TokenSalt = saltString,
            CreatedAt = DateTime.UtcNow
        };
        
        // Add to database
        _dbContext.Tokens.Add(tokenEntry);
        await _dbContext.SaveChangesAsync();
        
        // Save token to file
        await SaveTokenToFileAsync(username, token);
        
        return token;
    }
    
    // Verify if a token is valid
    public async Task<bool> VerifyTokenAsync(string token)
    {
        // Get active tokens
        var tokens = await _dbContext.Tokens
            .Where(t => t.RevokedAt == null)
            .ToListAsync();
        
        // Check each token
        foreach (var storedToken in tokens)
        {
            // Convert stored salt from string to bytes
            byte[] salt = Convert.FromBase64String(storedToken.TokenSalt);
            
            // Hash the provided token with the stored salt
            string hashedToken = HashToken(token, salt);
            
            // Compare hashed tokens
            if (hashedToken == storedToken.TokenHash)
            {
                return true;
            }
        }
        
        return false;
    }
    
    // Verify if a token is valid for a specific username
    public async Task<bool> VerifyTokenAsync(string token, string username)
    {
        // Get active tokens for this user
        var tokens = await _dbContext.Tokens
            .Where(t => t.Username == username && t.RevokedAt == null)
            .ToListAsync();
        
        // Check each token
        foreach (var storedToken in tokens)
        {
            // Convert stored salt from string to bytes
            byte[] salt = Convert.FromBase64String(storedToken.TokenSalt);
            
            // Hash the provided token with the stored salt
            string hashedToken = HashToken(token, salt);
            
            // Compare hashed tokens
            if (hashedToken == storedToken.TokenHash)
            {
                return true;
            }
        }
        
        return false;
    }
    
    // Helper method to hash a token
    private string HashToken(string token, byte[] salt)
    {
        using (var pbkdf2 = new Rfc2898DeriveBytes(token, salt, 10000, HashAlgorithmName.SHA256))
        {
            byte[] hash = pbkdf2.GetBytes(32); // 256 bits
            return Convert.ToBase64String(hash);
        }
    }
    
    // Helper method to generate a random salt
    private byte[] GenerateSalt()
    {
        byte[] salt = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(salt);
        }
        return salt;
    }
    
    // Helper method to save a token to a file
    private async Task SaveTokenToFileAsync(string username, string token)
    {
        try
        {
            string filePath = Path.Combine(_tokenFolderPath, $"{username}.txt");
            await File.WriteAllTextAsync(filePath, token);
            Log.Debug("🔑 Token file saved to {FilePath}", filePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Failed to save token file for user {Username}", username);
            throw;
        }
    }
    
    // Get all active tokens
    public async Task<IEnumerable<AuthToken>> GetActiveTokensAsync()
    {
        return await _dbContext.Tokens
            .Where(t => t.RevokedAt == null)
            .ToListAsync();
    }
    
    // Revoke a token by ID
    public async Task<bool> RevokeTokenAsync(int tokenId)
    {
        var token = await _dbContext.Tokens.FindAsync(tokenId);
        if (token == null) return false;
        
        token.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
        
        return true;
    }
}