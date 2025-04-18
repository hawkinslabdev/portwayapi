using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;

using Dapper;
using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using PortwayApi.Classes;
using PortwayApi.Helpers;
using PortwayApi.Interfaces;
using Serilog;

namespace PortwayApi.Api;

/// <summary>
/// Unified controller that handles all endpoint types (SQL, Proxy, Composite, Webhook)
/// </summary>
[ApiController]
[Route("api")] // Base route only, we'll use action-level routing
public class EndpointController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UrlValidator _urlValidator;
    private readonly EnvironmentSettings _environmentSettings;
    private readonly IODataToSqlConverter _oDataToSqlConverter;
    private readonly IEnvironmentSettingsProvider _environmentSettingsProvider;
    private readonly CompositeEndpointHandler _compositeHandler;

    public EndpointController(
        IHttpClientFactory httpClientFactory,
        UrlValidator urlValidator,
        EnvironmentSettings environmentSettings,
        IODataToSqlConverter oDataToSqlConverter,
        IEnvironmentSettingsProvider environmentSettingsProvider,
        CompositeEndpointHandler compositeHandler)
    {
        _httpClientFactory = httpClientFactory;
        _urlValidator = urlValidator;
        _environmentSettings = environmentSettings;
        _oDataToSqlConverter = oDataToSqlConverter;
        _environmentSettingsProvider = environmentSettingsProvider;
        _compositeHandler = compositeHandler;
    }

    /// <summary>
    /// Handles GET requests to endpoints
    /// </summary>
    [HttpGet("{env}/{**catchall}")]
    [ResponseCache(Duration = 300, VaryByQueryKeys = new[] { "*" })]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> GetAsync(
        string env,
        string catchall,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int top = 10,
        [FromQuery(Name = "$skip")] int skip = 0)
    {
        try
        {
            // Process the catchall to determine what type of endpoint we're dealing with
            var (endpointType, endpointName, remainingPath) = ParseEndpoint(catchall);
            
            Log.Debug("🔄 Processing {Type} endpoint: {Name}", endpointType, endpointName);

            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            switch (endpointType)
            {
                case EndpointType.SQL:
                    return await HandleSqlGetRequest(env, endpointName, select, filter, orderby, top, skip);
                case EndpointType.Proxy:
                    return await HandleProxyRequest(env, endpointName, remainingPath, "GET");
                case EndpointType.Composite:
                    Log.Warning("❌ Composite endpoints don't support GET requests");
                    return StatusCode(405, new { error = "Method not allowed for composite endpoints" });
                case EndpointType.Webhook:
                    Log.Warning("❌ Webhook endpoints don't support GET requests");
                    return StatusCode(405, new { error = "Method not allowed for webhook endpoints" });
                default:
                    Log.Warning("❌ Unknown endpoint type for {EndpointName}", endpointName);
                    return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing GET request for {Path}", Request.Path);
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Handles POST requests to endpoints
    /// </summary>
    [HttpPost("{env}/{**catchall}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PostAsync(
        string env,
        string catchall)
    {
        try
        {
            // Process the catchall to determine what type of endpoint we're dealing with
            var (endpointType, endpointName, remainingPath) = ParseEndpoint(catchall);
            
            Log.Debug("🔄 Processing {Type} endpoint: {Name} for POST", endpointType, endpointName);

            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            // Read the request body - we'll need it for several endpoint types
            Request.EnableBuffering();
            string requestBody;
            using (var reader = new StreamReader(Request.Body, leaveOpen: true))
            {
                requestBody = await reader.ReadToEndAsync();
            }
            // Reset position for further reading if needed
            Request.Body.Position = 0;
            
            switch (endpointType)
            {
                case EndpointType.SQL:
                    var data = JsonSerializer.Deserialize<JsonElement>(requestBody);
                    return await HandleSqlPostRequest(env, endpointName, data);
                    
                case EndpointType.Proxy:
                    return await HandleProxyRequest(env, endpointName, remainingPath, "POST");
                    
                case EndpointType.Composite:
                    // Strip off the "composite/" prefix from the endpoint name if needed
                    string actualCompositeName = endpointName.Replace("composite/", "");
                    return await HandleCompositeRequest(env, actualCompositeName, requestBody);
                    
                case EndpointType.Webhook:
                    var webhookData = JsonSerializer.Deserialize<JsonElement>(requestBody);
                    return await HandleWebhookRequest(env, endpointName, webhookData);
                    
                default:
                    Log.Warning("❌ Unknown endpoint type for {EndpointName}", endpointName);
                    return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing POST request for {Path}", Request.Path);
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Handles PUT requests to endpoints
    /// </summary>
    [HttpPut("{env}/{**catchall}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PutAsync(
        string env,
        string catchall)
    {
        try
        {
            // Process the catchall to determine what type of endpoint we're dealing with
            var (endpointType, endpointName, remainingPath) = ParseEndpoint(catchall);
            
            Log.Debug("🔄 Processing {Type} endpoint: {Name} for PUT", endpointType, endpointName);

            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            // Read the request body for SQL endpoint
            if (endpointType == EndpointType.SQL)
            {
                string requestBody;
                using (var reader = new StreamReader(Request.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
                
                var data = JsonSerializer.Deserialize<JsonElement>(requestBody);
                return await HandleSqlPutRequest(env, endpointName, data);
            }
            else if (endpointType == EndpointType.Proxy)
            {
                return await HandleProxyRequest(env, endpointName, remainingPath, "PUT");
            }
            else
            {
                // Composite and Webhook endpoints don't support PUT
                Log.Warning("❌ {Type} endpoints don't support PUT requests", endpointType);
                return StatusCode(405, new { error = $"Method not allowed for {endpointType} endpoints" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing PUT request for {Path}", Request.Path);
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Handles DELETE requests to endpoints
    /// </summary>
    [HttpDelete("{env}/{**catchall}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteAsync(
        string env,
        string catchall,
        [FromQuery] string? id = null)
    {
        try
        {
            // Process the catchall to determine what type of endpoint we're dealing with
            var (endpointType, endpointName, remainingPath) = ParseEndpoint(catchall);
            
            Log.Debug("🔄 Processing {Type} endpoint: {Name} for DELETE", endpointType, endpointName);

            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            switch (endpointType)
            {
                case EndpointType.SQL:
                    // Ensure ID is provided for SQL DELETE
                    if (string.IsNullOrEmpty(id))
                    {
                        return BadRequest(new { 
                            error = "ID parameter is required for delete operations", 
                            success = false 
                        });
                    }
                    
                    return await HandleSqlDeleteRequest(env, endpointName, id);
                    
                case EndpointType.Proxy:
                    return await HandleProxyRequest(env, endpointName, remainingPath, "DELETE");
                    
                case EndpointType.Composite:
                case EndpointType.Webhook:
                    Log.Warning("❌ {Type} endpoints don't support DELETE requests", endpointType);
                    return StatusCode(405, new { error = $"Method not allowed for {endpointType} endpoints" });
                    
                default:
                    Log.Warning("❌ Unknown endpoint type for {EndpointName}", endpointName);
                    return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing DELETE request for {Path}", Request.Path);
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Handles PATCH requests to endpoints
    /// </summary>
    [HttpPatch("{env}/{**catchall}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PatchAsync(
        string env,
        string catchall)
    {
        try
        {
            // Process the catchall to determine what type of endpoint we're dealing with
            var (endpointType, endpointName, remainingPath) = ParseEndpoint(catchall);
            
            Log.Debug("🔄 Processing {Type} endpoint: {Name} for PATCH", endpointType, endpointName);

            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            // Currently only proxy endpoints support PATCH
            if (endpointType == EndpointType.Proxy)
            {
                return await HandleProxyRequest(env, endpointName, remainingPath, "PATCH");
            }
            
            Log.Warning("❌ {Type} endpoints don't support PATCH requests", endpointType);
            return StatusCode(405, new { error = $"Method not allowed for {endpointType} endpoints" });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing PATCH request for {Path}", Request.Path);
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    #region Helper Methods and Handlers

    /// <summary>
    /// Parses the catchall segment to determine endpoint type and name
    /// </summary>
    private (EndpointType Type, string Name, string RemainingPath) ParseEndpoint(string catchall)
    {
        // Split catchall into segments
        var segments = catchall.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return (EndpointType.Standard, string.Empty, string.Empty);
        }
        
        string endpointName = segments[0];
        string remainingPath = string.Join('/', segments.Skip(1));
        
        // Special handling for composite endpoints
        if (endpointName.Equals("composite", StringComparison.OrdinalIgnoreCase))
        {
            // For composite endpoints, the second segment is the actual name
            if (segments.Length > 1)
            {
                endpointName = $"composite/{segments[1]}";
                remainingPath = string.Join('/', segments.Skip(2));
            }
            return (EndpointType.Composite, endpointName, remainingPath);
        }
        
        // Special handling for webhook endpoints
        if (endpointName.Equals("webhook", StringComparison.OrdinalIgnoreCase))
        {
            return (EndpointType.Webhook, remainingPath, string.Empty);
        }
        
        // Check if this is a SQL endpoint
        var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
        if (sqlEndpoints.ContainsKey(endpointName))
        {
            return (EndpointType.SQL, endpointName, remainingPath);
        }
        
        // Check if this is a proxy endpoint
        var proxyEndpoints = EndpointHandler.GetEndpoints(
            Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy")
        );
        
        if (proxyEndpoints.ContainsKey(endpointName))
        {
            return (EndpointType.Proxy, endpointName, remainingPath);
        }
        
        // Default to standard endpoint type if not recognized
        return (EndpointType.Standard, endpointName, remainingPath);
    }

    /// <summary>
    /// Handles proxy requests for any HTTP method with request caching
    /// </summary>
    private static readonly MemoryCache _proxyCache = new MemoryCache(new MemoryCacheOptions());
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _cacheLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

    private async Task<IActionResult> HandleProxyRequest(
        string env,
        string endpointName,
        string remainingPath,
        string method)
    {
        Log.Debug("🌍 Handling proxy request: {Endpoint} {Method}", endpointName, method);

        try
        {
            // Load proxy endpoints
            var proxyEndpoints = EndpointHandler.GetEndpoints(
                Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy")
            );

            // Find the endpoint configuration
            if (!proxyEndpoints.TryGetValue(endpointName, out var endpointConfig))
            {
                Log.Warning("❌ Endpoint not found: {EndpointName}", endpointName);
                return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }

            // Check if method is allowed
            if (!endpointConfig.Methods.Contains(method))
            {
                Log.Warning("❌ Method {Method} not allowed for endpoint {EndpointName}", 
                    method, endpointName);
                return StatusCode(405);
            }

            // Construct full URL
            var queryString = Request.QueryString.Value ?? "";
            var encodedPath = Uri.EscapeDataString(remainingPath);
            var fullUrl = $"{endpointConfig.Url}{(string.IsNullOrEmpty(remainingPath) ? "" : $"/{encodedPath}")}{queryString}";

            // Store the target URL in the context items for logging
            HttpContext.Items["TargetUrl"] = fullUrl;

            // Validate URL safety
            if (!_urlValidator.IsUrlSafe(fullUrl))
            {
                Log.Warning("🚫 Blocked potentially unsafe URL: {Url}", fullUrl);
                return StatusCode(403);
            }

            // For GET requests, try to use cache
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                // Create a cache key based on the request details
                string cacheKey = CreateCacheKey(env, endpointName, remainingPath, queryString, Request.Headers);
                
                // Try to get from cache first (without lock)
                if (_proxyCache.TryGetValue(cacheKey, out ProxyCacheEntry? cacheEntry) && cacheEntry != null)
                {
                    Log.Debug("📋 Cache hit for proxy request: {Endpoint}, URL: {Url}", endpointName, fullUrl);
                    
                    // Apply cached headers and status code
                    foreach (var header in cacheEntry.Headers)
                    {
                        Response.Headers[header.Key] = header.Value;
                    }
                    
                    Response.StatusCode = cacheEntry.StatusCode;
                    
                    // Write cached content
                    await Response.WriteAsync(cacheEntry.Content);
                    
                    return new EmptyResult(); // Response already written
                }
                
                // Get or create a lock for this cache key
                var cacheLock = _cacheLocks.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
                
                // Acquire lock to prevent duplicate requests for the same resource
                await cacheLock.WaitAsync();
                try
                {
                    // Double-check cache after acquiring lock
                    if (_proxyCache.TryGetValue(cacheKey, out cacheEntry) && cacheEntry != null)
                    {
                        Log.Debug("📋 Cache hit after lock for proxy request: {Endpoint}", endpointName);
                        
                        // Apply cached headers and status code
                        foreach (var header in cacheEntry.Headers)
                        {
                            Response.Headers[header.Key] = header.Value;
                        }
                        
                        Response.StatusCode = cacheEntry.StatusCode;
                        
                        // Write cached content
                        await Response.WriteAsync(cacheEntry.Content);
                        
                        return new EmptyResult(); // Response already written
                    }
                    
                    Log.Debug("🔍 Cache miss for proxy request: {Endpoint}, URL: {Url}", endpointName, fullUrl);
                    
                    // Continue with normal proxy process for cache miss
                    var responseDetails = await ExecuteProxyRequest(method, fullUrl, env, endpointConfig, endpointName);
                    
                    // For successful responses, store in cache
                    if (responseDetails.IsSuccessful)
                    {
                        // Determine cache duration - default to 5 minutes if not specified
                        TimeSpan cacheDuration = TimeSpan.FromMinutes(5);
                        
                        // Check for Cache-Control max-age directive
                        if (responseDetails.Headers.TryGetValue("Cache-Control", out var cacheControl))
                        {
                            var maxAgeMatch = Regex.Match(cacheControl, @"max-age=(\d+)");
                            if (maxAgeMatch.Success && int.TryParse(maxAgeMatch.Groups[1].Value, out int maxAge))
                            {
                                cacheDuration = TimeSpan.FromSeconds(maxAge);
                            }
                        }
                        
                        // Store response in cache
                        var entry = new ProxyCacheEntry
                        {
                            Content = responseDetails.Content,
                            Headers = responseDetails.Headers,
                            StatusCode = responseDetails.StatusCode
                        };
                        
                        var cacheOptions = new MemoryCacheEntryOptions()
                            .SetAbsoluteExpiration(cacheDuration)
                            .RegisterPostEvictionCallback((key, value, reason, state) => 
                            {
                                // Clean up the lock when the cache entry is removed
                                if (_cacheLocks.TryRemove((string)key, out var _))
                                {
                                    Log.Debug("🧹 Removed lock for expired cache entry: {Key}", key);
                                }
                            });
                        
                        _proxyCache.Set(cacheKey, entry, cacheOptions);
                        Log.Debug("💾 Cached proxy response for: {Endpoint} ({Duration} seconds)", 
                            endpointName, cacheDuration.TotalSeconds);
                    }
                    
                    return new EmptyResult(); // Response already written
                }
                finally
                {
                    cacheLock.Release();
                }
            }
            else
            {
                // For non-GET requests, just execute the proxy request without caching
                await ExecuteProxyRequest(method, fullUrl, env, endpointConfig, endpointName);
                return new EmptyResult(); // Response already written
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during proxy request: {EndpointName}", endpointName);

            return Problem(
                detail: $"Error processing endpoint {endpointName}: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Cache entry for proxy responses
    /// </summary>
    private class ProxyCacheEntry
    {
        public string Content { get; set; } = string.Empty;
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public int StatusCode { get; set; } = 200;
    }

    /// <summary>
    /// Creates a cache key based on request details
    /// </summary>
    private string CreateCacheKey(string env, string endpointName, string path, string queryString, IHeaderDictionary headers)
    {
        var keyBuilder = new StringBuilder();
        keyBuilder.Append($"{env}:{endpointName}:{path}:{queryString}");
        
        // Include authorization to differentiate between users if needed
        if (headers.TryGetValue("Authorization", out var authValues))
        {
            // Hash the token to avoid storing sensitive data in memory
            using var sha = SHA256.Create();
            var authBytes = Encoding.UTF8.GetBytes(authValues.ToString());
            var hashBytes = sha.ComputeHash(authBytes);
            var authHash = Convert.ToBase64String(hashBytes);
            
            keyBuilder.Append($":auth:{authHash}");
        }
        
        // Include other headers that might affect the response
        if (headers.TryGetValue("Accept-Language", out var langValues))
        {
            keyBuilder.Append($":lang:{langValues}");
        }
        
        return keyBuilder.ToString();
    }

    /// <summary>
    /// Executes the actual proxy request and writes the response
    /// </summary>
    private async Task<(bool IsSuccessful, string Content, Dictionary<string, string> Headers, int StatusCode)> ExecuteProxyRequest(
        string method, string fullUrl, string env, 
        (string Url, HashSet<string> Methods, bool IsPrivate, string Type) endpointConfig,
        string endpointName)
    {
        // Create HttpClient
        var client = _httpClientFactory.CreateClient("ProxyClient");

        // Create request message
        var requestMessage = new HttpRequestMessage(
            new HttpMethod(method), 
            fullUrl
        );

        // Copy request body for methods that can have body content
        if (HttpMethods.IsPost(method) ||
            HttpMethods.IsPut(method) ||
            HttpMethods.IsPatch(method) ||
            HttpMethods.IsDelete(method))
        {
            // Enable buffering to allow multiple reads
            Request.EnableBuffering();
            
            // Read the request body
            var memoryStream = new MemoryStream();
            await Request.Body.CopyToAsync(memoryStream);
            
            // Reset position for potential downstream middleware
            memoryStream.Position = 0;
            Request.Body.Position = 0;
            
            // Set the request content
            requestMessage.Content = new StreamContent(memoryStream);
            
            // Copy content type header if present
            if (Request.ContentType != null)
            {
                requestMessage.Content.Headers.ContentType = 
                    new System.Net.Http.Headers.MediaTypeHeaderValue(Request.ContentType);
            }
        }

        // Copy headers
        foreach (var header in Request.Headers)
        {
            // Skip certain headers that shouldn't be proxied
            if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                header.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    continue; // Already handled for request content

                requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not add header {HeaderKey}", header.Key);
            }
        }

        // Add custom headers
        requestMessage.Headers.Add("DatabaseName", env);
        requestMessage.Headers.Add("ServerName", Environment.MachineName);

        // Send the request
        var response = await client.SendAsync(requestMessage);
        
        // Store response headers for cache and apply to current response
        var responseHeaders = new Dictionary<string, string>();

        // Copy response headers
        foreach (var header in response.Headers)
        {
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                Response.Headers[header.Key] = header.Value.ToArray();
                responseHeaders[header.Key] = string.Join(",", header.Value);
            }
        }

        // Copy content headers, but exclude Content-Length
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                    responseHeaders[header.Key] = string.Join(",", header.Value);
                }
            }
        }
        
        // For GET requests, ensure Cache-Control header is set
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && !responseHeaders.ContainsKey("Cache-Control"))
        {
            // Add a default cache control header
            Response.Headers["Cache-Control"] = "public, max-age=300"; // 5 minutes
            responseHeaders["Cache-Control"] = "public, max-age=300";
        }

        // Set status code
        Response.StatusCode = (int)response.StatusCode;

        // Read and potentially rewrite response content
        var originalContent = response.Content != null
            ? await response.Content.ReadAsStringAsync()
            : string.Empty;

        // URL rewriting with your existing code
        if (!Uri.TryCreate(endpointConfig.Url, UriKind.Absolute, out var originalUri))
        {
            Log.Warning("❌ Could not parse endpoint URL as URI: {Url}", endpointConfig.Url);
            Response.StatusCode = 500;
            await Response.WriteAsync("Error processing request");
            return (false, string.Empty, responseHeaders, 500);
        }

        var originalHost = $"{originalUri.Scheme}://{originalUri.Host}:{originalUri.Port}";
        var originalPath = originalUri.AbsolutePath.TrimEnd('/');

        // Proxy path = /api/{env}/{endpoint}
        var proxyHost = $"{Request.Scheme}://{Request.Host}";
        var proxyPath = $"/api/{env}/{endpointName}";

        // Apply URL rewriting
        var rewrittenContent = UrlRewriter.RewriteUrl(
            originalContent, 
            originalHost, 
            originalPath, 
            proxyHost, 
            proxyPath);

        // Write the content to the response
        await Response.WriteAsync(rewrittenContent);

        Log.Debug("✅ Proxy request completed: {Method} {Path} -> {StatusCode}", 
            method, Request.Path, response.StatusCode);
            
        return (
            response.IsSuccessStatusCode, 
            rewrittenContent, 
            responseHeaders, 
            (int)response.StatusCode
        );
    }

    /// <summary>
    /// Handles composite endpoint requests
    /// </summary>
    private async Task<IActionResult> HandleCompositeRequest(
        string env,
        string endpointName,
        string requestBody)
    {
        try
        {
            Log.Information("🧩 Processing composite endpoint: {Endpoint}", endpointName);
            
            // Remove "composite/" prefix if present
            string compositeName = endpointName;
            if (endpointName.StartsWith("composite/", StringComparison.OrdinalIgnoreCase))
            {
                compositeName = endpointName.Substring("composite/".Length);
            }
            
            // Process the composite endpoint
            var result = await _compositeHandler.ProcessCompositeEndpointAsync(
                HttpContext, env, compositeName, requestBody);
                
            // Convert from IResult to IActionResult
            if (result is OkObjectResult okResult)
            {
                return Ok(okResult.Value);
            }
            else if (result is NotFoundObjectResult notFoundResult)
            {
                return NotFound(notFoundResult.Value);
            }
            else if (result is BadRequestObjectResult badRequestResult)
            {
                return BadRequest(badRequestResult.Value);
            }
            else if (result is ObjectResult objectResult)
            {
                return StatusCode(objectResult.StatusCode ?? 500, objectResult.Value);
            }
            else
            {
                // Default successful response
                return Ok(new { success = true, message = "Composite request processed" });
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during composite (proxy) request: {EndpointName}", endpointName);
            
            return Problem(
                detail: $"Error processing endpoint {endpointName}: {ex.Message}",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Handles webhook requests
    /// </summary>
    private async Task<IActionResult> HandleWebhookRequest(
        string env,
        string webhookId,
        JsonElement payload)
    {
        var requestUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        Log.Debug("📥 Webhook received: {Method} {Url}", Request.Method, requestUrl);

        try
        {
            // Validate environment and get connection string
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return BadRequest(new { 
                    error = "Database connection string is invalid or missing.", 
                    success = false 
                });
            }

            // Load webhook endpoint configuration
            var endpointConfig = EndpointHandler.GetSqlWebhookEndpoints()
                .FirstOrDefault(e => e.Key.Equals("Webhooks", StringComparison.OrdinalIgnoreCase)).Value;

            if (endpointConfig == null)
            {
                return NotFound(new { 
                    error = "Webhooks endpoint is not configured properly.", 
                    success = false 
                });
            }

            // Get table name and schema from the configuration
            var tableName = endpointConfig.DatabaseObjectName ?? "WebhookData";
            var schema = endpointConfig.DatabaseSchema ?? "dbo";

            // Validate webhook ID against allowed columns
            var allowedColumns = endpointConfig.AllowedColumns ?? new List<string>();
            if (allowedColumns.Any() && 
                !allowedColumns.Contains(webhookId, StringComparer.OrdinalIgnoreCase))
            {
                return NotFound(new { 
                    error = $"Webhook ID '{webhookId}' is not configured.", 
                    success = false 
                });
            }

            // Insert webhook data
            using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var insertQuery = $@"
                INSERT INTO [{schema}].[{tableName}] (WebhookId, Payload, ReceivedAt)
                OUTPUT INSERTED.Id
                VALUES (@WebhookId, @Payload, @ReceivedAt)";

            var insertedId = await connection.ExecuteScalarAsync<int>(insertQuery, new
            {
                WebhookId = webhookId,
                Payload = payload.ToString(),
                ReceivedAt = DateTime.UtcNow
            });

            Log.Information("✅ Webhook processed successfully: {WebhookId} (ID: {InsertedId})", 
                webhookId, insertedId);

            return Ok(new
            {
                message = "Webhook processed successfully.",
                id = insertedId
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during webhook processing: {WebhookId}", webhookId);
            
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }

    /// <summary>
    /// Helper method to build next link for pagination
    /// </summary>
    private string BuildNextLink(
        string env, 
        string endpointPath, 
        string? select, 
        string? filter, 
        string? orderby, 
        int top, 
        int skip)
    {
        var nextLink = $"/api/{env}/{endpointPath}?$top={top}&$skip={skip + top}";

        if (!string.IsNullOrWhiteSpace(select))
            nextLink += $"&$select={Uri.EscapeDataString(select)}";
        
        if (!string.IsNullOrWhiteSpace(filter))
            nextLink += $"&$filter={Uri.EscapeDataString(filter)}";
        
        if (!string.IsNullOrWhiteSpace(orderby))
            nextLink += $"&$orderby={Uri.EscapeDataString(orderby)}";

        return nextLink;
    }

    /// <summary>
    /// Helper method to convert JsonElement to appropriate parameter value
    /// </summary>
    private static object? GetParameterValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out int intValue) ? intValue 
                : element.TryGetDouble(out double doubleValue) ? doubleValue 
                : (object?)null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }

    /// <summary>
    /// Handles SQL GET requests
    /// </summary>
    private async Task<IActionResult> HandleSqlGetRequest(
        string env, 
        string endpointName, 
        string? select, 
        string? filter,
        string? orderby,
        int top,
        int skip)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
        Log.Debug("📥 SQL Query Request: {Url}", url);

        try
        {
            // Check if this is a SQL endpoint - if not, return 404
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointName))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration 
            var endpoint = sqlEndpoints[endpointName];

            // Step 3: Extract endpoint details
            var schema = endpoint.DatabaseSchema ?? "dbo";
            var objectName = endpoint.DatabaseObjectName;
            var allowedColumns = endpoint.AllowedColumns ?? new List<string>();
            var allowedMethods = endpoint.Methods ?? new List<string> { "GET" };

            // Step 4: Check if GET is allowed
            if (!allowedMethods.Contains("GET"))
            {
                return StatusCode(405);
            }

            // Step 5: Validate column names
            if (allowedColumns.Count > 0)
            {
                // Validate select columns
                if (!string.IsNullOrEmpty(select))
                {
                    var selectedColumns = select.Split(',')
                        .Select(c => c.Trim())
                        .ToList();

                    var invalidColumns = selectedColumns
                        .Where(col => !allowedColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (invalidColumns.Any())
                    {
                        return BadRequest(new { 
                            error = $"Selected columns not allowed: {string.Join(", ", invalidColumns)}", 
                            success = false 
                        });
                    }
                }
                else
                {
                    // If no select and columns are restricted, use allowed columns
                    select = string.Join(",", allowedColumns);
                }
            }

            // Step 6: Prepare OData parameters
            var odataParams = new Dictionary<string, string>
            {
                { "top", (top + 1).ToString() },
                { "skip", skip.ToString() }
            };

            if (!string.IsNullOrEmpty(select)) 
                odataParams["select"] = select;
            if (!string.IsNullOrEmpty(filter)) 
                odataParams["filter"] = filter;
            if (!string.IsNullOrEmpty(orderby)) 
                odataParams["orderby"] = orderby;

            // Step 7: Convert OData to SQL
            var (query, parameters) = _oDataToSqlConverter.ConvertToSQL($"{schema}.{objectName}", odataParams);

            // Step 8: Execute query
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var results = await connection.QueryAsync(query, parameters);
            var resultList = results.ToList();

            // Determine if it's the last page
            bool isLastPage = resultList.Count <= top;
            if (!isLastPage)
            {
                // Remove the extra row used for pagination
                resultList.RemoveAt(resultList.Count - 1);
            }

            // Step 9: Prepare response
            var response = new
            {
                Count = resultList.Count,
                Value = resultList,
                NextLink = isLastPage 
                    ? null 
                    : BuildNextLink(env, endpointName, select, filter, orderby, top, skip)
            };

            Log.Debug("✅ Successfully processed query for {Endpoint}", endpointName);
            return Ok(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during SQL query for endpoint: {EndpointName}", endpointName);
            
            return Problem(
                detail: $"Error processing. Please check the logs for more details.",
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Error"
            );
        }
    }
    
    /// <summary>
    /// Handles SQL POST requests (Create)
    /// </summary>
    private async Task<IActionResult> HandleSqlPostRequest(
        string env,
        string endpointName,
        JsonElement data)
    {
        try
        {
            // Check if this is a SQL endpoint
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointName))
            {
                return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }

            // Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return CreateErrorResponse($"Invalid or missing environment: {env}");
            }

            // Get endpoint configuration
            var endpoint = sqlEndpoints[endpointName];

            // Check method support and procedure definition
            if (!(endpoint.Methods?.Contains("POST") ?? false))
            {
                return CreateErrorResponse("This endpoint does not support POST operations", null, StatusCodes.Status405MethodNotAllowed);
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return CreateErrorResponse("This endpoint does not support insert operations (no procedure defined)");
            }

            // Validate input data against allowed columns
            var (isValid, errorMessage) = ValidateSqlInput(data, endpoint.AllowedColumns ?? new List<string>());
            if (!isValid)
            {
                return CreateErrorResponse(errorMessage!);
            }

            // Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            dynamicParams.Add("@Method", "INSERT");
            
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Extract and add parameters
            foreach (var property in data.EnumerateObject())
            {
                dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
            }

            // Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed INSERT procedure for {Endpoint}", endpointName);
            
            return Ok(new { 
                success = true,
                message = "Record created successfully", 
                result = resultList.FirstOrDefault() 
            });
        }
        catch (SqlException sqlEx)
        {
            // Handle SQL exceptions with sanitized details
            string errorMessage = "Database operation failed";
            string errorDetail;
            
            switch (sqlEx.Number)
            {
                case 2627:
                    errorDetail = "A record with the same unique identifier already exists";
                    break;
                case 547:
                    errorDetail = "The operation violates database constraints";
                    break;
                case 2601:
                    errorDetail = "A duplicate key value was attempted";
                    break;
                case 8114:
                    errorDetail = "Invalid data format provided for one or more fields";
                    break;
                case 4060:
                case 18456:
                    errorDetail = "Database access error";
                    Log.Error(sqlEx, "Database authentication error for {Endpoint}", endpointName);
                    break;
                default:
                    errorDetail = $"Database error code: {sqlEx.Number}";
                    Log.Error(sqlEx, "SQL Exception for {Endpoint}: {ErrorCode}, {ErrorMessage}", 
                        endpointName, sqlEx.Number, sqlEx.Message);
                    break;
            }
            
            return CreateErrorResponse(errorMessage, errorDetail, StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            string errorMessage = "An error occurred while processing your request";
            string? errorDetail = null;
            
            if (ex is JsonException)
            {
                errorMessage = "Invalid JSON format in request";
                errorDetail = "The request body contains malformed JSON";
                return CreateErrorResponse(errorMessage, errorDetail, StatusCodes.Status400BadRequest);
            }
            
            Log.Error(ex, "Error processing request for endpoint {EndpointName}: {ErrorType}: {ErrorMessage}", 
                endpointName, ex.GetType().Name, ex.Message);
            
            return CreateErrorResponse(errorMessage, null, StatusCodes.Status500InternalServerError);
        }
    }

    /// <summary>
    /// Handles SQL PUT requests (Update)
    /// </summary>
    private async Task<IActionResult> HandleSqlPutRequest(
        string env,
        string endpointName,
        JsonElement data)
    {
        try
        {
            // Check if this is a SQL endpoint - if not, return 404
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointName))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration
            var endpoint = sqlEndpoints[endpointName];

            // Step 3: Check if the endpoint supports PUT and has a procedure defined
            if (!(endpoint.Methods?.Contains("PUT") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support update operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Check if the ID is provided
            if (!data.TryGetProperty("id", out var idElement) && 
                !data.TryGetProperty("Id", out idElement) &&
                !data.TryGetProperty("ID", out idElement) &&
                !data.TryGetProperty("RequestId", out idElement))
            {
                return BadRequest(new { 
                    error = "ID property is required for update operations", 
                    success = false 
                });
            }

            // Step 5: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "UPDATE");
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 6: Extract and add data parameters from the request
            foreach (var property in data.EnumerateObject())
            {
                dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
            }

            // Step 7: Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name properly
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            // Convert result to a list (could be empty if no rows returned)
            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed UPDATE procedure for {Endpoint}", endpointName);
            
            // Return the results, which typically includes the updated record
            return Ok(new { 
                success = true,
                message = "Record updated successfully", 
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing UPDATE for {Endpoint}", endpointName);
            throw;
        }
    }

    /// <summary>
    /// Handles SQL DELETE requests
    /// </summary>
    private async Task<IActionResult> HandleSqlDeleteRequest(
        string env,
        string endpointName,
        string id)
    {
        try
        {
            // Check if this is a SQL endpoint - if not, return 404
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointName))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration
            var endpoint = sqlEndpoints[endpointName];

            // Step 3: Check if the endpoint supports DELETE and has a procedure defined
            if (!(endpoint.Methods?.Contains("DELETE") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support delete operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Check if the ID is provided
            if (string.IsNullOrEmpty(id))
            {
                return BadRequest(new { 
                    error = "ID parameter is required for delete operations", 
                    success = false 
                });
            }

            // Step 5: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "DELETE");
            dynamicParams.Add("@id", id);
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 6: Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name properly
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            // Convert result to a list (could be empty if no rows returned)
            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed DELETE procedure for {Endpoint}", endpointName);
            
            // Return the results, which typically includes deletion confirmation
            return Ok(new { 
                success = true,
                message = "Record deleted successfully", 
                id = id,
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing DELETE for {Endpoint}", endpointName);
            throw;
        }
    }


    /// <summary>
    /// Helper method to create a standard error response
    /// </summary>
    private IActionResult CreateErrorResponse(string message, string? detail = null, int statusCode = 400)
    {
        var response = new
        {
            success = false,
            error = message,
            errorDetail = detail,
            timestamp = DateTime.UtcNow
        };
        
        return StatusCode(statusCode, response);
    }

    /// <summary>
    /// Validates SQL input data against allowed columns
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateSqlInput(JsonElement data, List<string> allowedColumns)
    {
        // Check for empty request body
        if (data.ValueKind == JsonValueKind.Undefined || data.ValueKind == JsonValueKind.Null)
        {
            return (false, "Request body cannot be empty");
        }
        
        if (allowedColumns.Count > 0)
        {
            // Check if any properties in the request are not in allowed columns
            var invalidProperties = new List<string>();
            foreach (var property in data.EnumerateObject())
            {
                if (!allowedColumns.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                {
                    invalidProperties.Add(property.Name);
                }
            }
            
            if (invalidProperties.Any())
            {
                return (false, $"The following properties are not allowed: {string.Join(", ", invalidProperties)}");
            }
        }
        
        return (true, null);
    }

    /// <summary>
    /// Validates SQL parameters for update and delete operations
    /// </summary>
    private (bool IsValid, string? ErrorMessage) ValidateSqlParameters(JsonElement data, string operation)
    {
        // Check ID for update and delete operations
        if (operation is "UPDATE" or "DELETE")
        {
            bool hasId = data.TryGetProperty("id", out _) ||
                        data.TryGetProperty("Id", out _) ||
                        data.TryGetProperty("ID", out _) ||
                        data.TryGetProperty("RequestId", out _);
                        
            if (!hasId)
            {
                return (false, "ID field is required for this operation");
            }
        }
        
        // Validate data types for known fields (example for common fields)
        foreach (var property in data.EnumerateObject())
        {
            // Check date fields
            if (property.Name.EndsWith("Date", StringComparison.OrdinalIgnoreCase) && 
                property.Value.ValueKind == JsonValueKind.String)
            {
                if (!DateTime.TryParse(property.Value.GetString(), out _))
                {
                    return (false, $"Invalid date format for field: {property.Name}");
                }
            }
            
            // Check numeric fields
            if ((property.Name.EndsWith("Price", StringComparison.OrdinalIgnoreCase) || 
                property.Name.EndsWith("Amount", StringComparison.OrdinalIgnoreCase)) && 
                property.Value.ValueKind == JsonValueKind.String)
            {
                if (!decimal.TryParse(property.Value.GetString(), out _))
                {
                    return (false, $"Invalid numeric format for field: {property.Name}");
                }
            }
        }
        
        return (true, null);
    }

    public class RequestValidationMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestValidationMiddleware> _logger;

        public RequestValidationMiddleware(RequestDelegate next, ILogger<RequestValidationMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Check content type for POST/PUT/PATCH
            if (HttpMethods.IsPost(context.Request.Method) || 
                HttpMethods.IsPut(context.Request.Method) || 
                HttpMethods.IsPatch(context.Request.Method))
            {
                string? contentType = context.Request.ContentType;
                
                if (string.IsNullOrEmpty(contentType) || !contentType.Contains("application/json"))
                {
                    _logger.LogWarning("Invalid content type: {ContentType}", contentType);
                    
                    context.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                    context.Response.ContentType = "application/json";
                    
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Unsupported Media Type",
                        detail = "Request must use application/json content type",
                        success = false
                    });
                    
                    return;
                }
                
                // Check content length
                if (context.Request.ContentLength > 10_485_760) // 10MB limit
                {
                    _logger.LogWarning("Request body too large: {ContentLength} bytes", context.Request.ContentLength);
                    
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    context.Response.ContentType = "application/json";
                    
                    await context.Response.WriteAsJsonAsync(new
                    {
                        error = "Payload Too Large",
                        detail = "Request body exceeds maximum size of 10MB",
                        success = false
                    });
                    
                    return;
                }
            }

            await _next(context);
        }
    }
    #endregion
}