namespace PortwayApi.Middleware;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Serilog;

public class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    private readonly HashSet<string> _unsafeResponseHeaders;
    private readonly Dictionary<string, string> _additionalSecurityHeaders;

    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
        
        // Headers to remove
        _unsafeResponseHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Server",
            "X-AspNet-Version",
            "X-Powered-By",
            "X-SourceFiles",
            "X-AspNetMvc-Version"
        };
        
        // Headers to add
        _additionalSecurityHeaders = new Dictionary<string, string>
        {
            { "X-Content-Type-Options", "nosniff" },
            { "X-Frame-Options", "DENY" },
            { "Content-Security-Policy", "default-src 'self'" },
            { "Referrer-Policy", "strict-origin-when-cross-origin" },
            { "X-XSS-Protection", "1; mode=block" }
        };
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Register a callback that runs after the response headers are generated 
        // but before they're sent to the client
        context.Response.OnStarting(() =>
        {
            // Remove unsafe headers
            foreach (var header in _unsafeResponseHeaders)
            {
                if (context.Response.Headers.ContainsKey(header))
                {
                    context.Response.Headers.Remove(header);
                }
            }
            
            // Add security headers
            foreach (var header in _additionalSecurityHeaders)
            {
                if (!context.Response.Headers.ContainsKey(header.Key))
                {
                    context.Response.Headers[header.Key] = header.Value;
                }
            }
            
            return Task.CompletedTask;
        });

        // Call the next middleware in the pipeline
        await _next(context);
    }
}

/// <summary>
/// Extension method to make it easier to add the middleware to the pipeline
/// </summary>
public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<SecurityHeadersMiddleware>();
    }
}