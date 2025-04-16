using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PortwayApi.Classes;
using PortwayApi.Helpers;
using PortwayApi.Interfaces;
using Serilog;
using System.Data;

namespace PortwayApi.Api;

/// <summary>
/// Unified controller that handles all endpoint types (SQL, Proxy, Composite, Webhook)
/// to avoid routing conflicts
/// </summary>
[ApiController]
[Route("api/{env}/{**catchall}")]
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

    [HttpGet]
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
            
            Log.Information("🔄 Processing {Type} endpoint: {Name}", endpointType, endpointName);

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
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    /// <summary>
    /// Handles proxy requests for any HTTP method
    /// </summary>
    private async Task<IActionResult> HandleProxyRequest(
        string env,
        string endpointName,
        string remainingPath,
        string method)
    {
        Log.Information("🌍 Handling proxy request: {Endpoint} {Method}", endpointName, method);

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

            // Copy response headers
            foreach (var header in response.Headers)
            {
                if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
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
                    }
                }
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
                return StatusCode(500);
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

            // The Content-Length will be calculated automatically when writing the content
            await Response.WriteAsync(rewrittenContent);

            Log.Information("✅ Proxy request completed: {Method} {Path} -> {StatusCode}", 
                method, Request.Path, response.StatusCode);

            return new EmptyResult(); // The response has already been written
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during proxy request processing");
            throw;
        }
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
            Log.Error(ex, "❌ Error processing composite endpoint {EndpointName}", endpointName);
            throw;
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
            var endpointConfig = EndpointHandler.GetSqlEndpoints()
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

            Log.Information("✅ Webhook processed successfully: {WebhookId}, InsertedId: {InsertedId}", 
                webhookId, insertedId);

            return Ok(new
            {
                message = "Webhook processed successfully.",
                id = insertedId
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing webhook {WebhookId}", webhookId);
            throw;
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

    [HttpPost]
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
            
            Log.Information("🔄 Processing {Type} endpoint: {Name} for POST", endpointType, endpointName);

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
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpPut]
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
            
            Log.Information("🔄 Processing {Type} endpoint: {Name} for PUT", endpointType, endpointName);

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
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpDelete]
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
            
            Log.Information("🔄 Processing {Type} endpoint: {Name} for DELETE", endpointType, endpointName);

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
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpPatch]
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
            
            Log.Information("🔄 Processing {Type} endpoint: {Name} for PATCH", endpointType, endpointName);

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
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    #region Helper Methods

    /// <summary>
    /// Parses the catchall segment to determine endpoint type and name
    /// </summary>
    private (EndpointType Type, string Name, string RemainingPath) ParseEndpoint(string catchall)
    {
        // Default to proxy endpoint type
        var endpointType = EndpointType.Proxy;
        
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
        Log.Information("📥 SQL Query Request: {Url}", url);

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

            Log.Information("✅ Successfully processed query for {Endpoint}", endpointName);
            return Ok(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing SQL query for {Endpoint}", endpointName);
            throw;
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

            // Step 3: Check if the endpoint supports POST and has a procedure defined
            if (!(endpoint.Methods?.Contains("POST") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support insert operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "INSERT");
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 5: Extract and add data parameters from the request
            foreach (var property in data.EnumerateObject())
            {
                dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
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
            
            Log.Information("✅ Successfully executed INSERT procedure for {Endpoint}", endpointName);
            
            // Return the results, which typically includes the newly created ID
            return Ok(new { 
                success = true,
                message = "Record created successfully", 
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing INSERT for {Endpoint}", endpointName);
            throw;
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
    #endregion
}