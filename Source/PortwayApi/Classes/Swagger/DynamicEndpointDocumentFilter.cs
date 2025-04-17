using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        try {
            // Remove any controller-discovered paths that we'll be replacing
            RemoveConflictingPaths(swaggerDoc);
            
            // Generate unique operation IDs
            int operationIdCounter = 1;
            
            // Get allowed environments for parameter description
            var allowedEnvironments = GetAllowedEnvironments();
            
            // Add documentation for each endpoint type
            AddSqlEndpoints(swaggerDoc, allowedEnvironments, ref operationIdCounter);
            AddProxyEndpoints(swaggerDoc, allowedEnvironments, ref operationIdCounter);
            AddWebhookEndpoints(swaggerDoc, allowedEnvironments, ref operationIdCounter);
        }
        catch (Exception ex) {
            _logger.LogError(ex, "Error applying document filter");
        }
    }
    
    private void RemoveConflictingPaths(OpenApiDocument swaggerDoc)
    {
        var pathsToRemove = swaggerDoc.Paths
            .Where(p => p.Key.Contains("{catchall}") || p.Key.Contains("api/{env}"))
            .Select(p => p.Key)
            .ToList();
            
        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }
    
    private void AddSqlEndpoints(OpenApiDocument swaggerDoc, List<string> allowedEnvironments, ref int operationIdCounter)
    {
        // Get SQL endpoints
        var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
        
        foreach (var endpoint in sqlEndpoints)
        {
            string endpointName = endpoint.Key;
            var definition = endpoint.Value;
            
            // Path template for this endpoint
            string path = $"/api/{{env}}/{endpointName}";
            
            // Create path item if it doesn't exist
            if (!swaggerDoc.Paths.ContainsKey(path))
            {
                swaggerDoc.Paths[path] = new OpenApiPathItem();
            }
            
            // Add operations based on allowed methods
            var methods = definition.Methods;
            
            // Ensure GET is always included (even if not specified)
            if (!methods.Contains("GET", StringComparer.OrdinalIgnoreCase))
            {
                methods.Add("GET");
            }
            
            // Add each allowed operation
            foreach (var method in methods)
            {
                var opType = GetOperationType(method);
                if (opType == null) continue;
                
                var operation = CreateSqlOperation(
                    endpointName, 
                    method, 
                    definition, 
                    allowedEnvironments, 
                    operationIdCounter++);
                    
                swaggerDoc.Paths[path].Operations[opType.Value] = operation;
            }
            
            // Add specific delete endpoint with ID parameter
            if (methods.Contains("DELETE", StringComparer.OrdinalIgnoreCase))
            {
                var deletePath = $"/api/{{env}}/{endpointName}";
                
                // Create path item if it doesn't exist
                if (!swaggerDoc.Paths.ContainsKey(deletePath))
                {
                    swaggerDoc.Paths[deletePath] = new OpenApiPathItem();
                }
                
                var deleteOperation = CreateSqlDeleteOperation(
                    endpointName, 
                    definition, 
                    allowedEnvironments, 
                    operationIdCounter++);
                    
                swaggerDoc.Paths[deletePath].Operations[OperationType.Delete] = deleteOperation;
            }
        }
    }
    
    private void AddProxyEndpoints(OpenApiDocument swaggerDoc, List<string> allowedEnvironments, ref int operationIdCounter)
    {
        // Get proxy endpoints
        var proxyEndpoints = EndpointHandler.GetEndpoints(
            Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy"));
            
        foreach (var entry in proxyEndpoints)
        {
            string endpointName = entry.Key;
            var (url, methods, isPrivate, type) = entry.Value;
            
            // Skip private or composite endpoints
            if (isPrivate || type.Equals("Composite", StringComparison.OrdinalIgnoreCase))
                continue;
                
            // Path template for this endpoint
            string path = $"/api/{{env}}/{endpointName}";
            
            // Create path item if it doesn't exist
            if (!swaggerDoc.Paths.ContainsKey(path))
            {
                swaggerDoc.Paths[path] = new OpenApiPathItem();
            }
            
            // Add operations based on allowed methods
            foreach (var method in methods)
            {
                var opType = GetOperationType(method);
                if (opType == null) continue;
                
                var operation = CreateProxyOperation(
                    endpointName, 
                    method, 
                    url, 
                    allowedEnvironments, 
                    operationIdCounter++);
                    
                swaggerDoc.Paths[path].Operations[opType.Value] = operation;
            }
        }
    }
    
    private void AddWebhookEndpoints(OpenApiDocument swaggerDoc, List<string> allowedEnvironments, ref int operationIdCounter)
    {
        // Add webhook endpoint
        string path = "/webhook/{env}/{webhookId}";
        
        // Create path item if it doesn't exist
        if (!swaggerDoc.Paths.ContainsKey(path))
        {
            swaggerDoc.Paths[path] = new OpenApiPathItem();
        }
        
        // Create webhook POST operation
        var webhookOperation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = "Webhook" } },
            Summary = "Process incoming webhook",
            Description = "Receives and processes a webhook payload",
            OperationId = $"op_{operationIdCounter++}",
            Parameters = new List<OpenApiParameter>
            {
                new()
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList()
                    },
                    Description = "Target environment"
                },
                new()
                {
                    Name = "webhookId",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { Type = "string" },
                    Description = "Webhook identifier"
                }
            },
            RequestBody = new OpenApiRequestBody
            {
                Description = "Webhook payload (any valid JSON)",
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object"
                        }
                    }
                }
            },
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "Success" },
                ["400"] = new OpenApiResponse { Description = "Bad Request" },
                ["401"] = new OpenApiResponse { Description = "Unauthorized" },
                ["403"] = new OpenApiResponse { Description = "Forbidden" },
                ["500"] = new OpenApiResponse { Description = "Server Error" }
            }
        };
        
        swaggerDoc.Paths[path].Operations[OperationType.Post] = webhookOperation;
    }
    
    private OpenApiOperation CreateSqlOperation(
        string endpointName, 
        string method, 
        EndpointDefinition definition,
        List<string> allowedEnvironments,
        int operationId)
    {
        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = endpointName } }, // Assign unique tag based on endpoint name
            Summary = $"{method} {endpointName}",
            Description = $"{method} operation for {definition.DatabaseSchema}.{definition.DatabaseObjectName}",
            OperationId = $"op_{operationId}",
            Parameters = new List<OpenApiParameter>
            {
                // Environment parameter
                new()
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList()
                    },
                    Description = "Target environment"
                }
            }
        };
        
        // Add method-specific parameters and request body
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            // Add GET-specific parameters
            foreach (var parameter in new List<OpenApiParameter>
            {
                new()
                {
                    Name = "$select",
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = new OpenApiSchema { Type = "string" },
                    Description = "Select specific fields (comma-separated list of property names)"
                },
                new()
                {
                    Name = "$filter",
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = new OpenApiSchema { Type = "string" },
                    Description = "OData $filter expression"
                },
                new()
                {
                    Name = "$orderby",
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = new OpenApiSchema { Type = "string" },
                    Description = "OData $orderby expression"
                },
                new()
                {
                    Name = "$top",
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = new OpenApiSchema { 
                        Type = "integer", 
                        Default = new OpenApiInteger(10) 
                    },
                    Description = "Maximum number of records to return"
                },
                new()
                {
                    Name = "$skip",
                    In = ParameterLocation.Query,
                    Required = false,
                    Schema = new OpenApiSchema { 
                        Type = "integer", 
                        Default = new OpenApiInteger(0) 
                    },
                    Description = "Number of records to skip"
                }
            })
            {
                operation.Parameters.Add(parameter);
            }
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) || 
                 method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
        {
            // Add request body for POST and PUT
            operation.RequestBody = new OpenApiRequestBody
            {
                Description = method.Equals("POST") ? "Data for new record" : "Data for updated record",
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object"
                        }
                    }
                }
            };
        }
        
        // Add standard responses
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse { Description = "Success" },
            ["400"] = new OpenApiResponse { Description = "Bad Request" },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["403"] = new OpenApiResponse { Description = "Forbidden" },
            ["404"] = new OpenApiResponse { Description = "Not Found" },
            ["500"] = new OpenApiResponse { Description = "Server Error" }
        };
        
        return operation;
    }
    
    private OpenApiOperation CreateSqlDeleteOperation(
        string endpointName, 
        EndpointDefinition definition,
        List<string> allowedEnvironments,
        int operationId)
    {
        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = endpointName } }, // Assign unique tag based on endpoint name
            Summary = $"DELETE {endpointName}",
            Description = $"Delete operation for {definition.DatabaseSchema}.{definition.DatabaseObjectName}",
            OperationId = $"op_{operationId}",
            Parameters = new List<OpenApiParameter>
            {
                // Environment parameter
                new()
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList()
                    },
                    Description = "Target environment"
                },
                // ID parameter
                new()
                {
                    Name = "id",
                    In = ParameterLocation.Query,
                    Required = true,
                    Schema = new OpenApiSchema { Type = "string" },
                    Description = "ID of the record to delete"
                }
            },
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse { Description = "Success" },
                ["400"] = new OpenApiResponse { Description = "Bad Request" },
                ["401"] = new OpenApiResponse { Description = "Unauthorized" },
                ["403"] = new OpenApiResponse { Description = "Forbidden" },
                ["404"] = new OpenApiResponse { Description = "Not Found" },
                ["500"] = new OpenApiResponse { Description = "Server Error" }
            }
        };
        
        return operation;
    }
    
    private OpenApiOperation CreateProxyOperation(
        string endpointName, 
        string method, 
        string targetUrl,
        List<string> allowedEnvironments,
        int operationId)
    {
        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new() { Name = endpointName } }, // Assign unique tag based on endpoint name
            Summary = $"{method} {endpointName}",
            Description = $"Proxy {method} request to {targetUrl}",
            OperationId = $"op_{operationId}",
            Parameters = new List<OpenApiParameter>
            {
                // Environment parameter
                new()
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema
                    {
                        Type = "string",
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList()
                    },
                    Description = "Target environment"
                }
            }
        };
        
        // Add method-specific parameters and request body
        if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
        {
            // Add query parameters for GET
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "$filter",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = "string" },
                Description = "Filter expression"
            });
        }
        else if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) || 
                 method.Equals("PUT", StringComparison.OrdinalIgnoreCase) ||
                 method.Equals("PATCH", StringComparison.OrdinalIgnoreCase))
        {
            // Add request body for methods that support it
            operation.RequestBody = new OpenApiRequestBody
            {
                Description = "Request payload",
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object"
                        }
                    }
                }
            };
        }
        
        // Add standard responses
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse { Description = "Success" },
            ["400"] = new OpenApiResponse { Description = "Bad Request" },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["403"] = new OpenApiResponse { Description = "Forbidden" },
            ["404"] = new OpenApiResponse { Description = "Not Found" },
            ["500"] = new OpenApiResponse { Description = "Server Error" }
        };
        
        return operation;
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
    
    private OperationType? GetOperationType(string method)
    {
        return method.ToUpperInvariant() switch
        {
            "GET" => OperationType.Get,
            "POST" => OperationType.Post,
            "PUT" => OperationType.Put,
            "DELETE" => OperationType.Delete,
            "PATCH" => OperationType.Patch,
            "OPTIONS" => OperationType.Options,
            "HEAD" => OperationType.Head,
            "MERGE" => OperationType.Head, // OpenAPI doesn't have MERGE, use HEAD
            _ => null
        };
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
}