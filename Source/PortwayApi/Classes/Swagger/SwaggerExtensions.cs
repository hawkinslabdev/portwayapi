using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PortwayApi.Classes;

/// <summary>
/// Custom schema filter to handle recursive types in Swagger
/// </summary>
public class SwaggerSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        // Put schema filter logic here if needed
    }
}

/// <summary>
/// Helps fix issues with catch-all route parameters in Swagger documentation
/// </summary>
public class SwaggerDocumentFilter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Remove any catch-all route paths that can cause conflicts
        var paths = swaggerDoc.Paths.Keys.Where(p => p.Contains("{**")).ToList();
        foreach (var path in paths)
        {
            swaggerDoc.Paths.Remove(path);
        }

        // Remove any duplicate paths that might exist
        var duplicateKeys = swaggerDoc.Paths.Keys
            .GroupBy(path => path)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        foreach (var key in duplicateKeys)
        {
            swaggerDoc.Paths.Remove(key);
        }
    }
}

/// <summary>
/// Extends SwaggerGenOptions with custom configuration
/// </summary>
public static class SwaggerExtensions
{
    /// <summary>
    /// Configures Swagger generation to properly handle all API routes
    /// </summary>
    public static void ConfigureSwaggerGenWithCustomFilters(this SwaggerGenOptions options)
    {
        // Add document filter to handle catch-all routes
        options.DocumentFilter<SwaggerDocumentFilter>();
        options.DocumentFilter<SwaggerCatchAllParameterFilter>();
        options.OperationFilter<SwaggerOperationFilter>();
        
        // Add schema filter to handle recursive types
        options.SchemaFilter<SwaggerSchemaFilter>();
        
        // Handle conflicting routes by prioritizing the most specific ones
        options.ResolveConflictingActions(apiDescriptions => 
            apiDescriptions.OrderByDescending(apiDesc => GetRouteTemplateSpecificity(apiDesc)).First());
    }
    
    /// <summary>
    /// Calculate route template specificity to prioritize more specific routes
    /// </summary>
    private static int GetRouteTemplateSpecificity(ApiDescription apiDesc)
    {
        if (apiDesc.ActionDescriptor is not ControllerActionDescriptor actionDescriptor)
            return 0;
            
        // Calculate specificity based on:
        // 1. Number of literal segments
        // 2. Number of constrained parameter segments
        // 3. Penalize catch-all parameters
        var template = actionDescriptor.AttributeRouteInfo?.Template ?? string.Empty;
        
        // Count literal segments
        var literalSegments = template.Split('/')
            .Where(segment => !segment.StartsWith("{") && !string.IsNullOrEmpty(segment))
            .Count();
            
        // Count parameter segments
        var parameterSegments = template.Split('/')
            .Where(segment => segment.StartsWith("{") && !segment.Contains("**"))
            .Count();
            
        // Penalize routes with catch-all parameters
        var hasCatchAll = template.Contains("{**");
        
        // Calculate final score
        int score = (literalSegments * 10) + (parameterSegments * 5) - (hasCatchAll ? 100 : 0);
        
        return score;
    }
}