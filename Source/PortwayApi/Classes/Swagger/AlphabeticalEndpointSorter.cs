using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Collections.Generic;
using System.Linq;

namespace PortwayApi.Classes;

/// <summary>
/// Document filter that ensures endpoints are sorted alphabetically by path
/// rather than grouped by tag
/// </summary>
public class AlphabeticalEndpointSorter : IDocumentFilter
{
    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        var paths = swaggerDoc.Paths.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);
        
        swaggerDoc.Paths.Clear();
        foreach (var path in paths)
        {
            swaggerDoc.Paths.Add(path.Key, path.Value);
        }
        
    }
}
