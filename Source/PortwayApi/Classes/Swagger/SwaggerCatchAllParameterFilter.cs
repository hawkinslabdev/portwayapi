using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using Serilog;

namespace PortwayApi.Classes;

/// <summary>
/// Custom document filter to properly handle catchall parameters in Swagger
/// </summary>
public class SwaggerCatchAllParameterFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Find and remove any path with {**catchall} in it, as these are causing conflicts
        var pathsToRemove = swaggerDoc.Paths.Keys
            .Where(p => p.Contains("{**catchall}"))
            .ToList();

        foreach (var path in pathsToRemove)
        {
            Log.Debug($"Removing catchall path from Swagger: {path}");
            swaggerDoc.Paths.Remove(path);
        }
    }
}

/// <summary>
/// Custom operation filter to add security requirements to all operations
/// </summary>
public class SwaggerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        // Skip if this is a swagger operation
        if (context.ApiDescription.RelativePath?.StartsWith("swagger") == true)
        {
            return;
        }

        // Add security requirements to all operations
        operation.Security ??= new List<OpenApiSecurityRequirement>();
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                new string[] { }
            }
        });

        // Add standard response codes
        operation.Responses ??= new OpenApiResponses();
        
        if (!operation.Responses.ContainsKey("401"))
            operation.Responses.Add("401", new OpenApiResponse { Description = "Unauthorized" });
            
        if (!operation.Responses.ContainsKey("403"))
            operation.Responses.Add("403", new OpenApiResponse { Description = "Forbidden" });
            
        if (!operation.Responses.ContainsKey("500"))
            operation.Responses.Add("500", new OpenApiResponse { Description = "Server Error" });
    }
}