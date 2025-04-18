namespace PortwayApi.Middleware;

using System.Security.Claims;
using System.Text.Json;
using PortwayApi.Auth;
using Serilog;

public class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;

    public TokenAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, AuthDbContext dbContext, TokenService tokenService)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant();
        
        // Skip token validation for specific routes
        if (path?.StartsWith("/swagger") == true || 
            path == "/" ||
            path == "/index.html" ||
            path?.StartsWith("/health/live") == true ||
            context.Request.Path.StartsWithSegments("/favicon.ico"))
        {
            await _next(context);
            return;
        }
        
        // Continue with authentication logic
        Log.Debug("🔀 Incoming request: {Method} {Path}", context.Request.Method, context.Request.Path);

        if (!context.Request.Headers.TryGetValue("Authorization", out var providedToken))
        {
            Log.Warning("❌ Authorization header missing for {Path}", context.Request.Path);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Authentication required", success = false });
            return;
        }

        string tokenString = providedToken.ToString();
        
        // Extract the token from "Bearer token"
        if (tokenString.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            tokenString = tokenString.Substring("Bearer ".Length).Trim();
        }

        // Extract endpoint name from request path
        string? endpointName = ExtractEndpointName(context.Request.Path);
        
        // First validate token existence and active status
        bool isValid = await tokenService.VerifyTokenAsync(tokenString);

        if (!isValid)
        {
            Log.Warning("❌ Invalid or expired token used for {Path}", context.Request.Path);
            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired token", success = false });
            return;
        }
        
        // Get token details for context and scoped access check
        var tokenDetails = await tokenService.GetTokenDetailsByTokenAsync(tokenString);
        if (tokenDetails == null)
        {
            Log.Error("⚠️ Token verified but details could not be retrieved");
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Authentication error", success = false });
            return;
        }
        
        // Store token info in HttpContext.Items for potential use downstream
        context.Items["TokenInfo"] = new
        {
            Username = tokenDetails.Username,
            Scopes = tokenDetails.AllowedScopes,
            ExpiresAt = tokenDetails.ExpiresAt
        };
        
        // Set username claim in HttpContext.User.Identity for easy access elsewhere
        var identity = new ClaimsIdentity("Token");
        identity.AddClaim(new Claim(ClaimTypes.Name, tokenDetails.Username));
        
        // Add scopes as claims for potential authorization policies
        foreach (var scope in tokenDetails.GetScopesList())
        {
            identity.AddClaim(new Claim("scope", scope));
        }
        
        // Set the principal
        context.User = new ClaimsPrincipal(identity);
        
        // Check endpoint permissions if endpoint name was successfully extracted
        if (!string.IsNullOrEmpty(endpointName))
        {
            bool hasEndpointAccess = tokenDetails.HasAccessToEndpoint(endpointName);
            
            if (!hasEndpointAccess)
            {
                Log.Warning("❌ Token lacks permission for endpoint {Endpoint}. Available scopes: {Scopes}", 
                    endpointName, tokenDetails.AllowedScopes);
                
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new { 
                    error = $"Access denied to endpoint '{endpointName}'", 
                    availableScopes = tokenDetails.AllowedScopes,
                    requestedEndpoint = endpointName,
                    success = false 
                });
                return;
            }
        }

        // Token is valid and has proper scopes, proceed
        Log.Debug("✅ Authorized {User} for {Method} {Path}", tokenDetails.Username, context.Request.Method, context.Request.Path);
        await _next(context);
    }
    
    /// <summary>
    /// Extract the endpoint name from the request path
    /// </summary>
    private string? ExtractEndpointName(PathString path)
    {
        // Parse patterns like:
        // /api/{env}/{endpointName}
        // /api/{env}/{endpointName}/{id}
        // /api/{env}/composite/{endpointName}
        // /webhook/{env}/{webhookId}
        
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments == null || segments.Length < 3)
            return null;
            
        if (segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            // For regular endpoints: /api/{env}/{endpointName}
            if (segments.Length >= 3 && !segments[2].Equals("composite", StringComparison.OrdinalIgnoreCase))
            {
                return segments[2];
            }
            
            // For composite endpoints: /api/{env}/composite/{endpointName}
            if (segments.Length >= 4 && segments[2].Equals("composite", StringComparison.OrdinalIgnoreCase))
            {
                return $"composite/{segments[3]}";
            }
        }
        else if (segments[0].Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            // For webhook endpoints: /webhook/{env}/{webhookName}
            if (segments.Length >= 3)
            {
                return $"webhook/{segments[2]}";
            }
        }
        
        return null;
    }
}

// Extension method to make it easier to add the middleware to the pipeline
public static class TokenAuthMiddlewareExtensions
{
    public static IApplicationBuilder UseTokenAuthentication(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<TokenAuthMiddleware>();
    }
}