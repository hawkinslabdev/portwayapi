namespace PortwayApi.Middleware;

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
            path == "/health/live" ||
            context.Request.Path.StartsWithSegments("/favicon.ico"))
        {
            await _next(context);
            return;
        }
        
        // Continue with authentication logic
        Log.Debug("🔀 Incoming request: {Path}", context.Request.Path);

        if (!context.Request.Headers.TryGetValue("Authorization", out var providedToken))
        {
            Log.Warning("❌ Authorization header missing.");
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

        // Validate token
        bool isValid = await tokenService.VerifyTokenAsync(tokenString);

        if (!isValid)
        {
            Log.Warning("❌ Invalid token provided");
            context.Response.StatusCode = 403;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new { error = "Invalid token", success = false });
            return;
        }
    
        // Token is valid, proceed with the request
        Log.Debug("✅ Authorized request with valid token");

        await _next(context);
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