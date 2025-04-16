using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PortwayApi.Classes
{
    public class DynamicEndpointDocumentFilter : IDocumentFilter
    {
        private readonly ILogger<DynamicEndpointDocumentFilter> _logger;

        public DynamicEndpointDocumentFilter(ILogger<DynamicEndpointDocumentFilter> logger)
        {
            _logger = logger;
        }

        public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
        {
            try
            {
                // Remove conflicting paths with {catchall} or {env}/{catchall} in them
                var conflictingPaths = swaggerDoc.Paths.Keys
                    .Where(p => p.Contains("{**catchall}") || p.Contains("{env}/{catchall}") || p.Contains("{env}"))
                    .ToList();

                foreach (var path in conflictingPaths)
                {
                    Log.Debug($"Removing conflicting path from swagger: {path}");
                    swaggerDoc.Paths.Remove(path);
                }
                // Get endpoint map
                var endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
                
                // Get SQL endpoints
                var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
                
                // Get proxy endpoints
                var proxyEndpointsDirectory = Path.Combine(endpointsDirectory, "Proxy");
                var proxyEndpointMap = EndpointHandler.GetEndpoints(proxyEndpointsDirectory);
                
                // Get allowed environments for parameter description only
                var allowedEnvironments = GetAllowedEnvironments();

                // Log what we found - useful for debugging
                _logger.LogInformation("Found {SqlCount} SQL endpoints and {ProxyCount} proxy endpoints", 
                    sqlEndpoints.Count, proxyEndpointMap.Count);

                // Remove any existing paths with {catchall} in them
                var pathsToRemove = swaggerDoc.Paths.Keys
                    .Where(p => p.Contains("{catchall}"))
                    .ToList();

                foreach (var path in pathsToRemove)
                {
                    swaggerDoc.Paths.Remove(path);
                }

                // Add SQL endpoints
                foreach (var entry in sqlEndpoints)
                {
                    AddSqlEndpointToSwagger(swaggerDoc, entry.Key, entry.Value, allowedEnvironments);
                }

                // Add proxy endpoints
                foreach (var entry in proxyEndpointMap)
                {
                    string endpointName = entry.Key;
                    var (url, methods, isPrivate, type) = entry.Value;
                    
                    // Skip private endpoints and the Swagger/Refresh endpoint
                    if (isPrivate || string.Equals(endpointName, "Swagger", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping endpoint in Swagger docs: {EndpointName}", endpointName);
                        continue;
                    }
                    
                    // Skip composite endpoints as they'll be handled by CompositeEndpointDocumentFilter
                    if (string.Equals(type, "Composite", StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("Skipping composite endpoint in standard docs: {EndpointName}", endpointName);
                        continue;
                    }

                    AddProxyEndpointToSwagger(swaggerDoc, endpointName, url, methods, allowedEnvironments);
                }

                // Add webhook endpoints
                AddWebhookEndpointsToSwagger(swaggerDoc, allowedEnvironments);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying dynamic endpoint document filter");
            }
        }

        private void AddSqlEndpointToSwagger(OpenApiDocument swaggerDoc, string endpointName, 
            EndpointDefinition endpoint, List<string> allowedEnvironments)
        {
            // Create a generic path with {env} parameter
            string path = $"/api/{{env}}/{endpointName}";
            
            // If the path doesn't exist yet, create it
            if (!swaggerDoc.Paths.ContainsKey(path))
            {
                swaggerDoc.Paths.Add(path, new OpenApiPathItem());
            }

            // Add operations for each HTTP method
            var allowedMethods = endpoint.Methods ?? new List<string> { "GET" };
            foreach (var method in allowedMethods)
            {
                var operation = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "SQL Endpoints" } },
                    Summary = $"{method} {endpointName} SQL endpoint",
                    Description = $"SQL database endpoint for {endpoint.DatabaseSchema}.{endpoint.DatabaseObjectName}",
                    OperationId = $"{method.ToLower()}_{endpointName}".Replace(" ", "_"),
                    Parameters = new List<OpenApiParameter>()
                };

                // Add environment parameter
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { 
                        Type = "string", 
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList() 
                    },
                    Description = $"Environment to target. Allowed values: {string.Join(", ", allowedEnvironments)}"
                });

                // Add method-specific parameters
                switch (method.ToUpper())
                {
                    case "GET":
                        AddGetParameters(operation, endpoint);
                        break;
                    case "POST":
                        AddPostParameters(operation, endpoint);
                        break;
                    case "PUT":
                        AddPutParameters(operation, endpoint);
                        break;
                    case "DELETE":
                        AddDeleteParameters(operation, endpoint);
                        break;
                }

                // Add the operation to the path with the appropriate HTTP method
                AddOperationToPath(swaggerDoc.Paths[path], method, operation);
            }
        }

        private void AddProxyEndpointToSwagger(OpenApiDocument swaggerDoc, string endpointName, 
            string url, HashSet<string> methods, List<string> allowedEnvironments)
        {
            // Create a generic path with {env} parameter
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
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Proxy Endpoints" } },
                    Summary = $"{method} {endpointName} proxy endpoint",
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
                    Schema = new OpenApiSchema { 
                        Type = "string", 
                        Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList() 
                    },
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
                    
                    // Add $skip parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$skip",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { 
                            Type = "integer", 
                            Default = new OpenApiInteger(0)
                        },
                        Description = "Skip the specified number of results"
                    });
                    
                    // Add $orderby parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$orderby",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "Order the results (e.g., Name asc, Date desc)"
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
                                Schema = new OpenApiSchema { 
                                    Type = "object",
                                    Description = "Request JSON content for the proxy endpoint" 
                                }
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
                    },
                    ["401"] = new OpenApiResponse { Description = "Unauthorized" },
                    ["403"] = new OpenApiResponse { Description = "Forbidden" },
                    ["404"] = new OpenApiResponse { Description = "Not Found" },
                    ["500"] = new OpenApiResponse { Description = "Server Error" }
                };

                // Add the operation to the path with the appropriate HTTP method
                AddOperationToPath(swaggerDoc.Paths[path], method, operation);
            }
        }

        private void AddWebhookEndpointsToSwagger(OpenApiDocument swaggerDoc, List<string> allowedEnvironments)
        {
            try
            {
                var webhookEndpointsDir = Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Webhooks");
                if (!Directory.Exists(webhookEndpointsDir))
                {
                    return;
                }
                
                // Look for entity.json in the Webhooks directory
                var entityJsonPath = Path.Combine(webhookEndpointsDir, "entity.json");
                if (!File.Exists(entityJsonPath))
                {
                    return;
                }
                
                // Read webhook entity definition
                var json = File.ReadAllText(entityJsonPath);
                var webhookEntity = JsonSerializer.Deserialize<EndpointEntity>(json, 
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
                if (webhookEntity == null || webhookEntity.AllowedColumns == null || !webhookEntity.AllowedColumns.Any())
                {
                    return;
                }
                
                // Add a path for each allowed webhook ID
                foreach (var webhookId in webhookEntity.AllowedColumns)
                {
                    string path = $"/api/{{env}}/webhook/{webhookId}";
                    
                    if (!swaggerDoc.Paths.ContainsKey(path))
                    {
                        swaggerDoc.Paths.Add(path, new OpenApiPathItem());
                    }
                    
                    // Add POST operation
                    var operation = new OpenApiOperation
                    {
                        Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Webhooks" } },
                        Summary = $"Webhook endpoint: {webhookId}",
                        Description = $"Endpoint for receiving {webhookId} webhook data",
                        OperationId = $"post_webhook_{webhookId}",
                        Parameters = new List<OpenApiParameter>()
                    };
                    
                    // Add environment parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "env",
                        In = ParameterLocation.Path,
                        Required = true,
                        Schema = new OpenApiSchema { 
                            Type = "string", 
                            Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList() 
                        },
                        Description = $"Environment to target. Allowed values: {string.Join(", ", allowedEnvironments)}"
                    });
                    
                    // Add request body
                    operation.RequestBody = new OpenApiRequestBody
                    {
                        Required = true,
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { 
                                    Type = "object",
                                    Description = "Webhook payload" 
                                }
                            }
                        }
                    };
                    
                    // Add responses
                    operation.Responses = new OpenApiResponses
                    {
                        ["200"] = new OpenApiResponse 
                        { 
                            Description = "Webhook processed successfully",
                            Content = new Dictionary<string, OpenApiMediaType>
                            {
                                ["application/json"] = new OpenApiMediaType
                                {
                                    Schema = new OpenApiSchema { 
                                        Type = "object",
                                        Properties = new Dictionary<string, OpenApiSchema>
                                        {
                                            ["message"] = new OpenApiSchema { Type = "string" },
                                            ["id"] = new OpenApiSchema { Type = "integer" }
                                        }
                                    }
                                }
                            }
                        },
                        ["401"] = new OpenApiResponse { Description = "Unauthorized" },
                        ["400"] = new OpenApiResponse { Description = "Bad Request" },
                        ["500"] = new OpenApiResponse { Description = "Server Error" }
                    };
                    
                    // Add the operation to the path
                    swaggerDoc.Paths[path].Operations[OperationType.Post] = operation;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding webhook endpoints to Swagger");
            }
        }

        private void AddGetParameters(OpenApiOperation operation, EndpointDefinition endpoint)
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
            
            // Add $skip parameter
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "$skip",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { 
                    Type = "integer", 
                    Default = new OpenApiInteger(0)
                },
                Description = "Skip the specified number of results"
            });
            
            // Add $orderby parameter
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "$orderby",
                In = ParameterLocation.Query,
                Required = false,
                Schema = new OpenApiSchema { Type = "string" },
                Description = "Order the results (e.g., Name asc, Date desc)"
            });

            // Add responses
            operation.Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Success",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["count"] = new OpenApiSchema { Type = "integer" },
                                    ["value"] = new OpenApiSchema
                                    {
                                        Type = "array",
                                        Items = new OpenApiSchema { Type = "object" }
                                    },
                                    ["nextLink"] = new OpenApiSchema
                                    {
                                        Type = "string",
                                        Nullable = true
                                    }
                                }
                            }
                        }
                    }
                }
            };
        }

        private void AddPostParameters(OpenApiOperation operation, EndpointDefinition endpoint)
        {
            // Add request body
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Description = "Data to insert"
                        }
                    }
                }
            };

            // Add responses
            operation.Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Success",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["success"] = new OpenApiSchema { Type = "boolean" },
                                    ["message"] = new OpenApiSchema { Type = "string" },
                                    ["result"] = new OpenApiSchema { Type = "object" }
                                }
                            }
                        }
                    }
                }
            };
        }

        private void AddPutParameters(OpenApiOperation operation, EndpointDefinition endpoint)
        {
            // Add request body
            operation.RequestBody = new OpenApiRequestBody
            {
                Required = true,
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["application/json"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "object",
                            Description = "Data to update",
                            Properties = new Dictionary<string, OpenApiSchema>
                            {
                                ["id"] = new OpenApiSchema
                                {
                                    Type = "string",
                                    Description = "ID of the record to update",
                                    Required = new HashSet<string> { "id" }
                                }
                            }
                        }
                    }
                }
            };

            // Add responses
            operation.Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Success",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["success"] = new OpenApiSchema { Type = "boolean" },
                                    ["message"] = new OpenApiSchema { Type = "string" },
                                    ["result"] = new OpenApiSchema { Type = "object" }
                                }
                            }
                        }
                    }
                }
            };
        }

        private void AddDeleteParameters(OpenApiOperation operation, EndpointDefinition endpoint)
        {
            // Add id parameter
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = "id",
                In = ParameterLocation.Query,
                Required = true,
                Schema = new OpenApiSchema { Type = "string" },
                Description = "ID of the record to delete"
            });

            // Add responses
            operation.Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "Success",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["application/json"] = new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema
                            {
                                Type = "object",
                                Properties = new Dictionary<string, OpenApiSchema>
                                {
                                    ["success"] = new OpenApiSchema { Type = "boolean" },
                                    ["message"] = new OpenApiSchema { Type = "string" },
                                    ["id"] = new OpenApiSchema { Type = "string" },
                                    ["result"] = new OpenApiSchema { Type = "object" }
                                }
                            }
                        }
                    }
                }
            };
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
}