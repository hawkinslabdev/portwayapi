namespace PortwayApi.Classes;

using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

/// <summary>
/// Represents an endpoint entity with extended support for composite operations
/// </summary>
public class ExtendedEndpointEntity
{
    public string Url { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new List<string>();
    public string Type { get; set; } = "Standard"; // "Standard" or "Composite"
    public CompositeDefinition? CompositeConfig { get; set; }
    public bool IsPrivate { get; set; } = false; // If true, endpoint won't be exposed in the API
}

/// <summary>
/// Represents an endpoint entity with support for both proxy and SQL endpoints
/// </summary>
public class EndpointEntity
{
    // SQL endpoint properties
    public string? DatabaseObjectName { get; set; }
    public string? DatabaseSchema { get; set; }
    public List<string>? AllowedColumns { get; set; }
    public string? Procedure { get; set; }
    public List<string>? AllowedMethods { get; set; }
    
    // Proxy endpoint properties
    public string? Url { get; set; }
    public List<string>? Methods { get; set; }
    
    // Shared properties
    public bool IsPrivate { get; set; } = false;
    public string Type { get; set; } = "Standard"; // Standard, SQL, Composite
    public CompositeDefinition? CompositeConfig { get; set; }
}

/// <summary>
/// Defines the types of endpoints supported by the API
/// </summary>
public enum EndpointType
{
    /// <summary>
    /// Standard endpoint (fallback)
    /// </summary>
    Standard,
    
    /// <summary>
    /// SQL database endpoint
    /// </summary>
    SQL,
    
    /// <summary>
    /// Proxy endpoint to forward requests to another service
    /// </summary>
    Proxy,
    
    /// <summary>
    /// Composite endpoint that combines multiple operations
    /// </summary>
    Composite,
    
    /// <summary>
    /// Webhook endpoint for receiving external events
    /// </summary>
    Webhook,
    
    /// <summary>
    /// Private endpoint (not publicly accessible)
    /// </summary>
    Private
}