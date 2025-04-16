namespace PortwayApi.Endpoints;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Data.SqlClient;
using PortwayApi.Classes;
using PortwayApi.Helpers;
using PortwayApi.Interfaces;
using PortwayApi.Auth;
using System.Data;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Serilog;

public static class APIEndpointExtensions
{
    // SQL Endpoints
    public static WebApplication MapSQLEndpoints(this WebApplication app)
    {
        // Route pattern is explicitly tied to the configured SQL endpoints
        // to avoid conflicts with the proxy catchall handler
        app.MapGet("/api/{env}/{endpointPath}", async (
            HttpContext context,
            string env,
            string endpointPath,
            [FromServices] IODataToSqlConverter oDataToSqlConverter,
            [FromServices] IEnvironmentSettingsProvider environmentSettingsProvider,
            [FromQuery(Name = "$select")] string? select = null,
            [FromQuery(Name = "$filter")] string? filter = null,
            [FromQuery(Name = "$orderby")] string? orderby = null,
            [FromQuery(Name = "$top")] int top = 10,
            [FromQuery(Name = "$skip")] int skip = 0) =>
        {
            // Route constraint: Only proceed if this is a configured SQL endpoint
            // Check if the endpoint exists in the SQL endpoints configuration
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                // Not an SQL endpoint - delegate to the next handler
                return Results.Empty;
            }

            var url = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}{context.Request.QueryString}";
            Log.Information("📥 SQL Query Request: {Url}", url);

            try
            {
                // Load environment settings
                var (connectionString, _) = await environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
                
                // Get endpoint configuration
                var endpoint = sqlEndpoints[endpointPath];
                
                // Validate allowed methods
                if (!(endpoint.Methods?.Contains("GET") ?? false))
                    return Results.StatusCode(405);

                // Prepare OData parameters
                var odataParams = new Dictionary<string, string>
                {
                    { "top", (top + 1).ToString() },
                    { "skip", skip.ToString() }
                };

                // Handle column restrictions
                var allowedColumns = endpoint.AllowedColumns ?? new List<string>();
                if (allowedColumns.Any())
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
                            return Results.BadRequest(new { 
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

                if (!string.IsNullOrEmpty(select)) odataParams["select"] = select;
                if (!string.IsNullOrEmpty(filter)) odataParams["filter"] = filter;
                if (!string.IsNullOrEmpty(orderby)) odataParams["orderby"] = orderby;

                // Convert OData to SQL
                var (query, parameters) = oDataToSqlConverter.ConvertToSQL(
                    $"{endpoint.DatabaseSchema ?? "dbo"}.{endpoint.DatabaseObjectName}", 
                    odataParams
                );

                // Execute query
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                var results = await connection.QueryAsync(query, parameters);
                var resultList = results.ToList();

                // Handle pagination
                bool isLastPage = resultList.Count <= top;
                if (!isLastPage)
                {
                    resultList.RemoveAt(resultList.Count - 1);
                }

                // Prepare response
                var response = new
                {
                    Count = resultList.Count,
                    Value = resultList,
                    NextLink = isLastPage 
                        ? null 
                        : BuildNextLink(env, endpointPath, select, filter, orderby, top, skip)
                };

                // Mark this request as handled to prevent proxy processing
                context.Items["EndpointHandled"] = true;

                return Results.Ok(response);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing SQL query for {Endpoint}", endpointPath);
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        })
        .WithName("QuerySqlRecords");

        // SQL POST endpoint (Create)
        app.MapPost("/api/{env}/{endpointPath}", async (
            HttpContext context,
            string env,
            string endpointPath,
            [FromBody] JsonElement data,
            [FromServices] IEnvironmentSettingsProvider environmentSettingsProvider) =>
        {
            // Route constraint: Only proceed if this is a configured SQL endpoint
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                // Not an SQL endpoint - delegate to the next handler
                return Results.Empty;
            }

            try
            {
                // Load environment settings
                var (connectionString, _) = await environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
                
                // Get endpoint configuration
                var endpoint = sqlEndpoints[endpointPath];
                
                // Check if POST is allowed and procedure is configured
                if (!(endpoint.Methods?.Contains("POST") ?? false) || 
                    string.IsNullOrEmpty(endpoint.Procedure))
                    return Results.StatusCode(405);

                // Prepare stored procedure parameters
                var dynamicParams = new DynamicParameters();
                
                // Add method parameter
                dynamicParams.Add("@Method", "INSERT");

                // Add user parameter if available
                if (context.User?.Identity?.Name != null)
                {
                    dynamicParams.Add("@UserName", context.User.Identity.Name);
                }

                // Add data parameters from the request
                foreach (var property in data.EnumerateObject())
                {
                    dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
                }

                // Execute stored procedure
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                
                // Split procedure into schema and name
                var procedureParts = endpoint.Procedure.Split('.');
                var schema = procedureParts.Length > 1 ? procedureParts[0].Trim('[', ']') : "dbo";
                var procedureName = procedureParts.Length > 1 ? procedureParts[1].Trim('[', ']') : endpoint.Procedure.Trim('[', ']');

                var result = await connection.QueryAsync(
                    $"[{schema}].[{procedureName}]", 
                    dynamicParams, 
                    commandType: CommandType.StoredProcedure
                );

                // Mark this request as handled to prevent proxy processing
                context.Items["EndpointHandled"] = true;

                return Results.Ok(new { 
                    message = "Record inserted successfully", 
                    result = result.FirstOrDefault() 
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing SQL insert for {Endpoint}", endpointPath);
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        })
        .WithName("InsertSqlRecord");

        // SQL PUT endpoint (Update)
        app.MapPut("/api/{env}/{endpointPath}", async (
            HttpContext context,
            string env,
            string endpointPath,
            [FromBody] JsonElement data,
            [FromServices] IEnvironmentSettingsProvider environmentSettingsProvider) =>
        {
            // Route constraint: Only proceed if this is a configured SQL endpoint
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                // Not an SQL endpoint - delegate to the next handler
                return Results.Empty;
            }

            try
            {
                // Load environment settings
                var (connectionString, _) = await environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
                
                // Get endpoint configuration
                var endpoint = sqlEndpoints[endpointPath];
                
                // Check if PUT is allowed and procedure is configured
                if (!(endpoint.Methods?.Contains("PUT") ?? false) || 
                    string.IsNullOrEmpty(endpoint.Procedure))
                    return Results.StatusCode(405);

                // Prepare stored procedure parameters
                var dynamicParams = new DynamicParameters();
                
                // Add method parameter
                dynamicParams.Add("@Method", "UPDATE");

                // Add user parameter if available
                if (context.User?.Identity?.Name != null)
                {
                    dynamicParams.Add("@UserName", context.User.Identity.Name);
                }

                // Add data parameters from the request
                foreach (var property in data.EnumerateObject())
                {
                    dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
                }

                // Execute stored procedure
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                
                // Split procedure into schema and name
                var procedureParts = endpoint.Procedure.Split('.');
                var schema = procedureParts.Length > 1 ? procedureParts[0].Trim('[', ']') : "dbo";
                var procedureName = procedureParts.Length > 1 ? procedureParts[1].Trim('[', ']') : endpoint.Procedure.Trim('[', ']');

                var result = await connection.QueryAsync(
                    $"[{schema}].[{procedureName}]", 
                    dynamicParams, 
                    commandType: CommandType.StoredProcedure
                );

                // Mark this request as handled to prevent proxy processing
                context.Items["EndpointHandled"] = true;

                return Results.Ok(new { 
                    message = "Record updated successfully", 
                    result = result.FirstOrDefault() 
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing SQL update for {Endpoint}", endpointPath);
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        })
        .WithName("UpdateSqlRecord");

        // SQL DELETE endpoint
        app.MapDelete("/api/{env}/{endpointPath}", async (
            HttpContext context,
            string env,
            string endpointPath,
            [FromQuery] string id,
            [FromServices] IEnvironmentSettingsProvider environmentSettingsProvider) =>
        {
            // Route constraint: Only proceed if this is a configured SQL endpoint
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                // Not an SQL endpoint - delegate to the next handler
                return Results.Empty;
            }

            try
            {
                // Load environment settings
                var (connectionString, _) = await environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
                
                // Get endpoint configuration
                var endpoint = sqlEndpoints[endpointPath];
                
                // Check if DELETE is allowed and procedure is configured
                if (!(endpoint.Methods?.Contains("DELETE") ?? false) || 
                    string.IsNullOrEmpty(endpoint.Procedure))
                    return Results.StatusCode(405);

                // Prepare stored procedure parameters
                var dynamicParams = new DynamicParameters();
                
                // Add method parameter
                dynamicParams.Add("@Method", "DELETE");
                dynamicParams.Add("@id", id);

                // Add user parameter if available
                if (context.User?.Identity?.Name != null)
                {
                    dynamicParams.Add("@UserName", context.User.Identity.Name);
                }

                // Execute stored procedure
                using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                
                // Split procedure into schema and name
                var procedureParts = endpoint.Procedure.Split('.');
                var schema = procedureParts.Length > 1 ? procedureParts[0].Trim('[', ']') : "dbo";
                var procedureName = procedureParts.Length > 1 ? procedureParts[1].Trim('[', ']') : endpoint.Procedure.Trim('[', ']');

                var result = await connection.QueryAsync(
                    $"[{schema}].[{procedureName}]", 
                    dynamicParams, 
                    commandType: CommandType.StoredProcedure
                );

                // Mark this request as handled to prevent proxy processing
                context.Items["EndpointHandled"] = true;

                return Results.Ok(new { 
                    message = "Record deleted successfully", 
                    id = id,
                    result = result.FirstOrDefault() 
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing SQL delete for {Endpoint}", endpointPath);
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        })
        .WithName("DeleteSqlRecord");

        return app;
    }

    // Helper method to convert JsonElement to appropriate parameter value
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

    // Helper method to build next link for pagination
    private static string BuildNextLink(
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

    // Proxy Endpoints
    public static WebApplication MapProxyEndpoints(this WebApplication app)
    {
        app.Map("/api/{env}/{**catchall}", async (
            HttpContext context,
            string env,
            string? catchall,
            [FromServices] IHttpClientFactory httpClientFactory,
            [FromServices] UrlValidator urlValidator,
            [FromServices] EnvironmentSettings environmentSettings) =>
        {
            // Skip processing if this request has already been handled
            if (context.Response.HasStarted || context.Items.ContainsKey("EndpointHandled"))
                return Results.Empty;

            Log.Information("🌍 Received proxy request: {Path} {Method}", context.Request.Path, context.Request.Method);

            try
            {
                // Check if environment is allowed
                if (!environmentSettings.IsEnvironmentAllowed(env))
                {
                    Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                    return Results.BadRequest(new { error = $"Environment '{env}' is not allowed." });
                }

                // Parse endpoint from catchall
                var endpointParts = (catchall ?? "").Split('/');
                if (endpointParts.Length == 0)
                {
                    Log.Warning("❌ Invalid endpoint format in request: {Path}", context.Request.Path);
                    return Results.BadRequest(new { error = "Invalid endpoint format" });
                }

                var endpointName = endpointParts[0];
                var remainingPath = string.Join('/', endpointParts.Skip(1));

                // Load proxy endpoints
                var proxyEndpoints = EndpointHandler.GetEndpoints(
                    Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy")
                );

                // Find the endpoint configuration
                if (!proxyEndpoints.TryGetValue(endpointName, out var endpointConfig))
                {
                    // Mark that we attempted to handle this endpoint
                    context.Items["EndpointHandled"] = true;
                    
                    Log.Warning("❌ Endpoint not found: {EndpointName}", endpointName);
                    return Results.NotFound(new { error = $"Endpoint '{endpointName}' not found" });
                }

                // Check if method is allowed
                if (!endpointConfig.Methods.Contains(context.Request.Method))
                {
                    Log.Warning("❌ Method {Method} not allowed for endpoint {EndpointName}", 
                        context.Request.Method, endpointName);
                    return Results.StatusCode(405);
                }

                // Construct full URL
                var queryString = context.Request.QueryString.Value ?? "";
                var encodedPath = Uri.EscapeDataString(remainingPath);
                var fullUrl = $"{endpointConfig.Url}{(string.IsNullOrEmpty(remainingPath) ? "" : $"/{encodedPath}")}{queryString}";

                // Store the target URL in the context items for logging
                context.Items["TargetUrl"] = fullUrl;

                // Validate URL safety
                if (!urlValidator.IsUrlSafe(fullUrl))
                {
                    Log.Warning("🚫 Blocked potentially unsafe URL: {Url}", fullUrl);
                    return Results.StatusCode(403);
                }

                // Create HttpClient
                var client = httpClientFactory.CreateClient("ProxyClient");

                // Create request message
                var requestMessage = new HttpRequestMessage(
                    new HttpMethod(context.Request.Method), 
                    fullUrl
                );

                // Copy request body for methods that can have body content
                if (HttpMethods.IsPost(context.Request.Method) ||
                    HttpMethods.IsPut(context.Request.Method) ||
                    HttpMethods.IsPatch(context.Request.Method) ||
                    HttpMethods.IsDelete(context.Request.Method))
                {
                    var memoryStream = new MemoryStream();
                    await context.Request.Body.CopyToAsync(memoryStream);
                    memoryStream.Position = 0;
                    requestMessage.Content = new StreamContent(memoryStream);

                    // Copy content type header if present
                    if (context.Request.ContentType != null)
                    {
                        requestMessage.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(context.Request.ContentType);
                    }
                }

                // Copy headers
                foreach (var header in context.Request.Headers)
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
                    context.Response.Headers[header.Key] = header.Value.ToArray();
                }

                // Copy content headers
                if (response.Content != null)
                {
                    foreach (var header in response.Content.Headers)
                    {
                        context.Response.Headers[header.Key] = header.Value.ToArray();
                    }
                }

                // Set status code
                context.Response.StatusCode = (int)response.StatusCode;

                // Read and potentially rewrite response content
                var originalContent = response.Content != null
                    ? await response.Content.ReadAsStringAsync()
                    : string.Empty;

                // URL rewriting - using the UrlRewriter helper
                var rewrittenContent = UrlRewriter.RewriteUrl(
                    originalContent, 
                    endpointConfig.Url, 
                    remainingPath, 
                    $"{context.Request.Scheme}://{context.Request.Host}", 
                    $"/api/{env}/{endpointName}"
                );

                // Write the rewritten content
                await context.Response.WriteAsync(rewrittenContent);

                Log.Information("✅ Proxy request completed: {Method} {Path} -> {StatusCode}", 
                    context.Request.Method, context.Request.Path, response.StatusCode);

                // Mark that we handled this endpoint
                context.Items["EndpointHandled"] = true;
                return Results.Empty;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error during proxy request processing");
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        return app;
    }

    // Composite Endpoints
    public static WebApplication MapCompositeEndpoints(this WebApplication app)
    {
        app.Map("/api/{env}/composite/{endpointName}", async (
            HttpContext context,
            string env,
            string endpointName,
            [FromServices] CompositeEndpointHandler compositeHandler,
            [FromServices] EnvironmentSettings environmentSettings) =>
        {
            // Mark that this is a special endpoint type
            context.Items["EndpointType"] = "Composite";

            Log.Information("🧩 Received composite request: {Path} {Method}", 
                context.Request.Path, context.Request.Method);

            try
            {
                // Check environment
                if (!environmentSettings.IsEnvironmentAllowed(env))
                {
                    Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                    return Results.BadRequest(new { error = $"Environment '{env}' is not allowed." });
                }

                // Read the request body
                string requestBody;
                using (var reader = new StreamReader(context.Request.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
                
                // Process the composite endpoint
                var result = await compositeHandler.ProcessCompositeEndpointAsync(context, env, endpointName, requestBody);
                
                // Mark that we handled this endpoint
                context.Items["EndpointHandled"] = true;
                
                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error processing composite endpoint: {Error}", ex.Message);
                return Results.Problem(
                    detail: ex.Message, 
                    title: "Internal Server Error",
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        return app;
    }

    // Webhook Endpoints
    public static WebApplication MapWebhookEndpoints(this WebApplication app)
    {
        app.MapPost("/webhook/{env}/{webhookId}", async (
            HttpContext context,
            string env,
            string webhookId,
            [FromBody] JsonElement payload,
            [FromServices] IEnvironmentSettingsProvider environmentSettingsProvider) =>
        {
            var requestUrl = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.Path}";
            Log.Debug("📥 Webhook received: {Method} {Url}", context.Request.Method, requestUrl);

            try
            {
                // Validate environment and get connection string
                var (connectionString, serverName) = await environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);

                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    return Results.BadRequest(new { 
                        error = "Database connection string is invalid or missing.", 
                        success = false 
                    });
                }

                // Load webhook endpoint configuration
                var endpointConfig = EndpointHandler.GetSqlEndpoints()
                    .FirstOrDefault(e => e.Key.Equals("Webhooks", StringComparison.OrdinalIgnoreCase)).Value;

                if (endpointConfig == null)
                {
                    return Results.NotFound(new { 
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
                    return Results.NotFound(new { 
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

                return Results.Ok(new
                {
                    message = "Webhook processed successfully.",
                    id = insertedId
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error processing webhook {WebhookId}", webhookId);
                return Results.Problem(
                    detail: ex.Message, 
                    statusCode: StatusCodes.Status500InternalServerError
                );
            }
        });

        return app;
    }

    // Health Check Endpoints
    public static WebApplication MapHealthCheckEndpoints(this WebApplication app)
    {
        app.MapGet("/health", async (HttpContext context, 
                                     PortwayApi.Services.HealthCheckService healthService) =>
        {
            // Get cached health report
            var report = await healthService.CheckHealthAsync();
            
            // Add cache headers
            context.Response.Headers.CacheControl = "public, max-age=15";
            context.Response.Headers.Append("Expires", DateTime.UtcNow.AddSeconds(15).ToString("R"));
            
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = report.Status == Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy 
                ? StatusCodes.Status200OK 
                : StatusCodes.Status503ServiceUnavailable;
                
            await context.Response.WriteAsJsonAsync(new 
            { 
                status = report.Status.ToString(),
                timestamp = DateTime.UtcNow,
                cache_expires_in = "15 seconds" 
            });
        })
        .ExcludeFromDescription();

        app.MapGet("/health/live", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "public, max-age=5";
            context.Response.Headers.Append("Expires", DateTime.UtcNow.AddSeconds(5).ToString("R"));
            
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Alive");
        })
        .ExcludeFromDescription();

        app.MapGet("/health/details", async (HttpContext context, 
                                          PortwayApi.Services.HealthCheckService healthService) =>
        {
            // Get cached health report
            var report = await healthService.CheckHealthAsync();
            
            // Add cache headers
            context.Response.Headers.CacheControl = "public, max-age=60";
            context.Response.Headers.Append("Expires", DateTime.UtcNow.AddSeconds(60).ToString("R"));
            
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = report.Status switch
            {
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => StatusCodes.Status200OK,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => StatusCodes.Status200OK,
                Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError
            };
            
            var result = new
            {
                status = report.Status.ToString(),
                timestamp = DateTime.UtcNow,
                cache_expires_in = "60 seconds",
                checks = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    duration = $"{e.Value.Duration.TotalMilliseconds:F2}ms",
                    data = e.Value.Data.Count > 0 ? e.Value.Data : null,
                    tags = e.Value.Tags
                }),
                totalDuration = $"{report.TotalDuration.TotalMilliseconds:F2}ms",
                version = typeof(APIEndpointExtensions).Assembly.GetName().Version?.ToString() ?? "Unknown"
            };
            
            await context.Response.WriteAsJsonAsync(result);
        })
        .ExcludeFromDescription();

        return app;
    }
}