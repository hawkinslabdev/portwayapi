using Microsoft.AspNetCore.Mvc;
using PortwayApi.Classes;
using PortwayApi.Helpers;
using Serilog;
using System.Text;

namespace PortwayApi.Api;

// This controller handles proxy requests with a higher order value than SQL API
// so it will only be invoked for endpoints that don't match SQL endpoints
[ApiController]
[Route("api/{env}/{**catchall}")]
[ApiExplorerSettings(IgnoreApi = false)]
[ProxyConstraint] // Custom constraint to avoid routing conflicts
public class ProxyAPI : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UrlValidator _urlValidator;
    private readonly EnvironmentSettings _environmentSettings;

    public ProxyAPI(
        IHttpClientFactory httpClientFactory,
        UrlValidator urlValidator,
        EnvironmentSettings environmentSettings)
    {
        _httpClientFactory = httpClientFactory;
        _urlValidator = urlValidator;
        _environmentSettings = environmentSettings;
    }

    [HttpGet]
    [HttpPost]
    [HttpPut]
    [HttpDelete]
    [HttpPatch]
    public async Task<IActionResult> ProxyRequest(
        string env,
        string catchall)
    {
        Log.Information("🌍 Received proxy request: {Path} {Method}", Request.Path, Request.Method);

        try
        {
            // Check if environment is allowed
            if (!_environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            // Parse endpoint from catchall
            var endpointParts = (catchall ?? "").Split('/');
            if (endpointParts.Length == 0)
            {
                Log.Warning("❌ Invalid endpoint format in request: {Path}", Request.Path);
                return BadRequest(new { error = "Invalid endpoint format" });
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
                Log.Warning("❌ Endpoint not found: {EndpointName}", endpointName);
                return NotFound(new { error = $"Endpoint '{endpointName}' not found" });
            }

            // Check if method is allowed
            if (!endpointConfig.Methods.Contains(Request.Method))
            {
                Log.Warning("❌ Method {Method} not allowed for endpoint {EndpointName}", 
                    Request.Method, endpointName);
                return StatusCode(405);
            }

            // Construct full URL
            var queryString = Request.QueryString.Value ?? "";
            var encodedPath = Uri.EscapeDataString(remainingPath);
            var fullUrl = $"{endpointConfig.Url}{(string.IsNullOrEmpty(remainingPath) ? "" : $"/{encodedPath}")}{queryString}";

            // Store for logging
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
                new HttpMethod(Request.Method), 
                fullUrl
            );

            // Copy request body for methods that can have body content
            if (HttpMethods.IsPost(Request.Method) ||
                HttpMethods.IsPut(Request.Method) ||
                HttpMethods.IsPatch(Request.Method) ||
                HttpMethods.IsDelete(Request.Method))
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

            // Prepare response
            var responseMessage = new HttpResponseMessage(response.StatusCode);

            // Copy response headers
            foreach (var header in response.Headers)
            {
                Response.Headers[header.Key] = header.Value.ToArray();
            }

            // Copy content headers
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    Response.Headers[header.Key] = header.Value.ToArray();
                }
            }

            // Set status code
            Response.StatusCode = (int)response.StatusCode;

            // Read and potentially rewrite response content
            var originalContent = response.Content != null
                ? await response.Content.ReadAsStringAsync()
                : string.Empty;

            // URL rewriting - using the UrlRewriter helper
            var rewrittenContent = UrlRewriter.RewriteUrl(
                originalContent, 
                endpointConfig.Url, 
                remainingPath, 
                $"{Request.Scheme}://{Request.Host}", 
                $"/api/{env}/{endpointName}"
            );

            // Write the rewritten content
            await Response.WriteAsync(rewrittenContent);

            Log.Information("✅ Proxy request completed: {Method} {Path} -> {StatusCode}", 
                Request.Method, Request.Path, response.StatusCode);

            return new EmptyResult(); // The response has already been written
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error during proxy request processing");
            return Problem(
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }
}

// Custom constraint for proxy endpoint routing
// This allows the route to be skipped when SQL endpoints should handle the request
public class ProxyConstraintAttribute : Attribute, IRouteConstraint
{
    public bool Match(
        HttpContext httpContext, 
        IRouter route, 
        string routeKey, 
        RouteValueDictionary values, 
        RouteDirection routeDirection)
    {
        // Extract the endpoint path from the route values
        if (!values.TryGetValue("catchall", out var catchallObj) || catchallObj == null)
        {
            return false;
        }

        string catchall = catchallObj.ToString() ?? "";
        
        // Extract the first segment as the endpoint name
        var segments = catchall.Split('/');
        if (segments.Length == 0)
        {
            return true; // Let the controller handle invalid formats
        }

        string endpointName = segments[0];
        
        // Check if this endpoint is defined as an SQL endpoint
        var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
        bool isSqlEndpoint = sqlEndpoints.ContainsKey(endpointName);
        
        // Return true (match) only if it's NOT an SQL endpoint
        return !isSqlEndpoint;
    }
}