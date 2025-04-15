namespace PortwayApi.Helpers;

using System;
using System.Text.RegularExpressions;
using Serilog;

/// <summary>
/// Helper class for rewriting URLs in API responses
/// </summary>
public static class UrlRewriter
{
    /// <summary>
    /// Rewrites URLs in a JSON string to use proxy URLs
    /// </summary>
    /// <param name="content">The original content with URLs</param>
    /// <param name="originalBaseUrl">The original base URL to replace</param>
    /// <param name="originalPath">The original path segment</param>
    /// <param name="newBaseUrl">The new base URL</param>
    /// <param name="newPath">The new path</param>
    /// <returns>Content with rewritten URLs</returns>
    public static string RewriteUrl(
        string content, 
        string originalBaseUrl, 
        string originalPath, 
        string newBaseUrl, 
        string newPath)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(originalBaseUrl))
        {
            return content;
        }

        try
        {
            // Clean up URLs for consistent replacement
            originalBaseUrl = originalBaseUrl.TrimEnd('/');
            newBaseUrl = newBaseUrl.TrimEnd('/');
            newPath = newPath.TrimEnd('/');

            // Build the full URLs
            var fullOriginalUrl = string.IsNullOrEmpty(originalPath) 
                ? originalBaseUrl
                : $"{originalBaseUrl}/{originalPath}";
                
            var fullNewUrl = $"{newBaseUrl}{newPath}";

            // Create regex patterns to handle various URL formats in JSON
            // This handles both quoted and unquoted URLs
            
            // 1. Replace full URLs (with scheme, host, port, path)
            var fullUrlPattern = @"(\"")?(" + Regex.Escape(fullOriginalUrl) + @"(\/[^\""\s]*)?)(\""|[\s,}])";
            content = Regex.Replace(content, fullUrlPattern, m => {
                var hasQuotes = m.Groups[1].Success;
                var path = m.Groups[3].Value;
                var end = m.Groups[4].Value;
                
                return (hasQuotes ? "\"" : "") + fullNewUrl + path + end;
            });
            
            // 2. Replace base URL only (when paths might be dynamic)
            var baseUrlPattern = @"(\"")?(" + Regex.Escape(originalBaseUrl) + @")(\/[^\""\s]*)(\""|[\s,}])";
            content = Regex.Replace(content, baseUrlPattern, m => {
                var hasQuotes = m.Groups[1].Success;
                var path = m.Groups[3].Value;
                var end = m.Groups[4].Value;
                
                return (hasQuotes ? "\"" : "") + newBaseUrl + newPath + path + end;
            });

            // Handle relative URLs if needed
            try {
                // Extract original domain path for relative URL handling
                var uri = new Uri(originalBaseUrl);
                var originalDomain = uri.Host;
                
                // 3. Replace domain references that might be in the content
                if (!string.IsNullOrEmpty(originalDomain)) {
                    var domainPattern = @"([""\'])(" + Regex.Escape(originalDomain) + @")([""\'])";
                    
                    // Extract new domain from new base URL
                    var newUri = new Uri(newBaseUrl);
                    var newDomain = newUri.Host;
                    
                    content = Regex.Replace(content, domainPattern, m => {
                        var startQuote = m.Groups[1].Value;
                        var endQuote = m.Groups[3].Value;
                        return startQuote + newDomain + endQuote;
                    });
                }
            } catch (UriFormatException) {
                // Skip domain replacement if URLs cannot be parsed
            }

            return content;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error rewriting URLs in content from {OriginalUrl} to {NewUrl}", originalBaseUrl, newBaseUrl);
            return content; // Return original content if rewriting fails
        }
    }
}