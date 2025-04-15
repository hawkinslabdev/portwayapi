using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PortwayApi.Classes;

public class DynamicEndpointDocumentFilter : IDocumentFilter
{
    private readonly ILogger<DynamicEndpointDocumentFilter> _logger;

    public DynamicEndpointDocumentFilter(ILogger<DynamicEndpointDocumentFilter> logger)
    {
        _logger = logger;
    }

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Get endpoint map
        var endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
        var endpointMap = EndpointHandler.GetEndpoints(endpointsDirectory);
        
        // Get allowed environments for parameter description only
        var allowedEnvironments = GetAllowedEnvironments();

        // Create paths for each endpoint - but only once, not per environment
        foreach (var entry in endpointMap)
        {
            string endpointName = entry.Key;
            var (url, methods, isPrivate, type) = entry.Value;
            
            // Skip Swagger/Refresh endpoint
            if (string.Equals(endpointName, "Swagger", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            
            // Skip private endpoints - they're for internal use only
            if (isPrivate)
            {
                _logger.LogDebug("Skipping private endpoint in Swagger docs: {EndpointName}", endpointName);
                continue;
            }
            
            // Skip composite endpoints as they'll be handled by CompositeEndpointDocumentFilter
            if (string.Equals(type, "Composite", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Skipping composite endpoint in standard docs: {EndpointName}", endpointName);
                continue;
            }

            // Create a generic path with {env} parameter instead of specific environments
            string path = $"/api/{{env}}/{endpointName}";
            
            // If the path doesn't exist yet, create it
            if (!swaggerDoc.Paths.ContainsKey(path))
            {
                swaggerDoc.Paths.Add(path, new OpenApiPathItem());
            }

            // Add operations for each HTTP method
            foreach (var method in methods)
            {
                var operation = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = endpointName } },
                    Summary = $"{method} {endpointName} endpoint",
                    Description = $"Proxies {method} requests to {url}",
                    OperationId = $"{method.ToLower()}_{endpointName}".Replace(" ", "_"),
                    Parameters = new List<OpenApiParameter>()
                };

                // Add environment parameter
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { Type = "string", Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList() },
                    Description = $"Environment to target. Allowed values: {string.Join(", ", allowedEnvironments)}"
                });

                // Add OData style query parameters for GET requests
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    // Add $select parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$select",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "Select specific fields (comma-separated list of property names)"
                    });

                    // Add $top parameter with default value
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$top",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { 
                            Type = "integer", 
                            Default = new OpenApiInteger(10),
                            Minimum = 1,
                            Maximum = 1000
                        },
                        Description = "Limit the number of results returned (default: 10, max: 1000)"
                    });

                    // Add $filter parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$filter",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "Filter the results based on a condition (e.g., Name eq 'Value')"
                    });
                }
                
                // Add request body for methods that support it
                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) || 
                    method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                    method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
                {
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Type = "object" }
                            }
                        }
                    };
                }

                // Add example response
                operation.Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse 
                    { 
                        Description = "Successful response",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Type = "object" }
                            }
                        }
                    }
                };

                // Add the operation to the path with the appropriate HTTP method
                AddOperationToPath(swaggerDoc.Paths[path], method, operation);
            }
        }

        // Remove any paths with {catchall} in them
        var pathsToRemove = swaggerDoc.Paths.Keys
            .Where(p => p.Contains("{catchall}"))
            .ToList();

        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }

    private List<string> GetAllowedEnvironments()
    {
        try
        {
            var settingsFile = Path.Combine(Directory.GetCurrentDirectory(), "environments", "settings.json");
            if (File.Exists(settingsFile))
            {
                var settingsJson = File.ReadAllText(settingsFile);
                
                // Match the structure used in EnvironmentSettings class
                var settings = JsonSerializer.Deserialize<SettingsModel>(settingsJson);
                if (settings?.Environment?.AllowedEnvironments != null && 
                    settings.Environment.AllowedEnvironments.Any())
                {
                    return settings.Environment.AllowedEnvironments;
                }
            }
            
            // Return default if settings not found
            return new List<string> { "600", "700" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading environment settings");
            return new List<string> { "600", "700" };
        }
    }

    // Match the classes used in EnvironmentSettings
    private class SettingsModel
    {
        public EnvironmentModel Environment { get; set; } = new EnvironmentModel();
    }

    private class EnvironmentModel
    {
        public string ServerName { get; set; } = ".";
        public List<string> AllowedEnvironments { get; set; } = new List<string>();
    }

    private void AddOperationToPath(OpenApiPathItem pathItem, string method, OpenApiOperation operation)
    {
        switch (method.ToUpper())
        {
            case "GET":
                pathItem.Operations[OperationType.Get] = operation;
                break;
            case "POST":
                pathItem.Operations[OperationType.Post] = operation;
                break;
            case "PUT":
                pathItem.Operations[OperationType.Put] = operation;
                break;
            case "DELETE":
                pathItem.Operations[OperationType.Delete] = operation;
                break;
            case "PATCH":
                pathItem.Operations[OperationType.Patch] = operation;
                break;
            case "OPTIONS":
                pathItem.Operations[OperationType.Options] = operation;
                break;
            case "MERGE":
                // Note: OpenAPI doesn't have a native MERGE operation type
                // We'll use Head as it's less commonly used
                pathItem.Operations[OperationType.Head] = operation;
                break;        
        }
    }
}