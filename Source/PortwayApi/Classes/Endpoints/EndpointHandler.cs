namespace PortwayApi.Classes;

using System.Text.Json;
using Serilog;

/// <summary>
/// Unified endpoint definition that handles all endpoint types
/// </summary>
public class EndpointDefinition
{
    public string Url { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new List<string>();
    public EndpointType Type { get; set; } = EndpointType.Standard;
    public CompositeDefinition? CompositeConfig { get; set; }
    public bool IsPrivate { get; set; } = false;
    
    // SQL endpoint properties
    public string? DatabaseObjectName { get; set; }
    public string? DatabaseSchema { get; set; }
    public List<string>? AllowedColumns { get; set; }
    public string? Procedure { get; set; }

    // Helper properties to simplify type checking
    public bool IsStandard => Type == EndpointType.Standard && !IsPrivate;
    public bool IsComposite => Type == EndpointType.Composite || 
                              (CompositeConfig != null && !string.IsNullOrEmpty(CompositeConfig.Name));
    public bool IsSql => Type == EndpointType.SQL;
                              
    // Helper method to get a consistent tuple format compatible with existing code
    public (string Url, HashSet<string> Methods, bool IsPrivate, string Type) ToTuple()
    {
        string typeString = this.Type.ToString();
        return (Url, new HashSet<string>(Methods, StringComparer.OrdinalIgnoreCase), IsPrivate, typeString);
    }
}

public static class EndpointHandler
{
    // Cache for loaded endpoints to avoid multiple loads
    private static Dictionary<string, EndpointDefinition>? _loadedProxyEndpoints = null;
    private static Dictionary<string, EndpointDefinition>? _loadedSqlEndpoints = null;
    private static readonly object _loadLock = new object();
    
    /// <summary>
    /// Gets SQL endpoints from the /endpoints/SQL directory
    /// </summary>
    public static Dictionary<string, EndpointDefinition> GetSqlEndpoints()
    {
        string sqlEndpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "SQL");
        LoadSqlEndpointsIfNeeded(sqlEndpointsDirectory);
        return _loadedSqlEndpoints!;
    }
    
    /// <summary>
    /// Gets all composite endpoint definitions from the endpoints directory
    /// </summary>
    public static Dictionary<string, CompositeDefinition> GetCompositeDefinitions(Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> endpointMap)
    {
        // We already have endpoints loaded, so just extract the composite configs
        var compositeDefinitions = new Dictionary<string, CompositeDefinition>(StringComparer.OrdinalIgnoreCase);
        
        // If proxy endpoints haven't been loaded yet, load them
        if (_loadedProxyEndpoints == null)
        {
            string proxyEndpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy");
            LoadProxyEndpointsIfNeeded(proxyEndpointsDirectory);
        }
        
        foreach (var kvp in _loadedProxyEndpoints!)
        {
            if (kvp.Value.IsComposite && kvp.Value.CompositeConfig != null)
            {
                compositeDefinitions[kvp.Key] = kvp.Value.CompositeConfig;
            }
        }
        
        return compositeDefinitions;
    }
    
    /// <summary>
    /// Scans the specified directory for endpoint definition files and returns a dictionary of endpoints.
    /// </summary>
    /// <param name="endpointsDirectory">Directory containing endpoint definitions</param>
    /// <returns>Dictionary with endpoint names as keys and tuples of (url, methods, isPrivate, type) as values</returns>
    public static Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> GetEndpoints(string endpointsDirectory)
    {
        // Check if the directory is for proxy or SQL endpoints
        bool isProxyEndpoint = endpointsDirectory.Contains("Proxy", StringComparison.OrdinalIgnoreCase);
        
        // Load endpoints if not already loaded
        if (isProxyEndpoint)
        {
            LoadProxyEndpointsIfNeeded(endpointsDirectory);
            
            // Convert to the legacy format
            var endpointMap = new Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _loadedProxyEndpoints!)
            {
                endpointMap[kvp.Key] = kvp.Value.ToTuple();
            }
            
            return endpointMap;
        }
        else
        {
            // Create an empty dictionary for now - SQL endpoints are handled differently
            return new Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)>(StringComparer.OrdinalIgnoreCase);
        }
    }
    
    /// <summary>
    /// Internal method to load proxy endpoints if they haven't been loaded yet
    /// </summary>
    private static void LoadProxyEndpointsIfNeeded(string endpointsDirectory)
    {
        // Use double-check locking pattern to ensure thread safety
        if (_loadedProxyEndpoints == null)
        {
            lock (_loadLock)
            {
                if (_loadedProxyEndpoints == null)
                {
                    _loadedProxyEndpoints = LoadProxyEndpoints(endpointsDirectory);
                }
            }
        }
    }
    
    /// <summary>
    /// Internal method to load SQL endpoints if they haven't been loaded yet
    /// </summary>
    private static void LoadSqlEndpointsIfNeeded(string endpointsDirectory)
    {
        // Use double-check locking pattern to ensure thread safety
        if (_loadedSqlEndpoints == null)
        {
            lock (_loadLock)
            {
                if (_loadedSqlEndpoints == null)
                {
                    _loadedSqlEndpoints = LoadSqlEndpoints(endpointsDirectory);
                }
            }
        }
    }
    
    /// <summary>
    /// Internal method to load all proxy endpoints from the endpoints directory
    /// </summary>
    private static Dictionary<string, EndpointDefinition> LoadProxyEndpoints(string endpointsDirectory)
    {
        var endpoints = new Dictionary<string, EndpointDefinition>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Log.Warning($"⚠️ Proxy endpoints directory not found: {endpointsDirectory}");
                Directory.CreateDirectory(endpointsDirectory);
                return endpoints;
            }

            // Get all JSON files in the endpoints directory and subdirectories
            foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    // Read and parse the endpoint definition
                    var json = File.ReadAllText(file);
                    var definition = ParseProxyEndpointDefinition(json);
                    
                    if (definition != null && !string.IsNullOrWhiteSpace(definition.Url) && definition.Methods.Any())
                    {
                        // Extract endpoint name from directory name
                        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                        
                        // Skip if no valid name could be extracted
                        if (string.IsNullOrWhiteSpace(endpointName))
                        {
                            Log.Warning("⚠️ Could not determine endpoint name for {File}", file);
                            continue;
                        }

                        // Add the endpoint to the dictionary
                        endpoints[endpointName] = definition;
                        
                        LogEndpointLoading(endpointName, definition);
                    }
                    else
                    {
                        Log.Warning("⚠️ Failed to load endpoint from {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Error parsing endpoint file: {File}", file);
                }
            }

            Log.Information($"✅ Loaded {endpoints.Count} proxy endpoints from {endpointsDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error scanning proxy endpoints directory: {Directory}", endpointsDirectory);
        }

        return endpoints;
    }
    
    /// <summary>
    /// Internal method to load all SQL endpoints from the endpoints directory
    /// </summary>
    private static Dictionary<string, EndpointDefinition> LoadSqlEndpoints(string endpointsDirectory)
    {
        var endpoints = new Dictionary<string, EndpointDefinition>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Log.Warning($"⚠️ SQL endpoints directory not found: {endpointsDirectory}");
                Directory.CreateDirectory(endpointsDirectory);
                return endpoints;
            }

            // Get all JSON files in the endpoints directory and subdirectories
            foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    // Read and parse the endpoint definition
                    var json = File.ReadAllText(file);
                    var definition = ParseSqlEndpointDefinition(json);
                    
                    if (definition != null && !string.IsNullOrWhiteSpace(definition.DatabaseObjectName))
                    {
                        // Extract endpoint name from directory name
                        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                        
                        // Skip if no valid name could be extracted
                        if (string.IsNullOrWhiteSpace(endpointName))
                        {
                            Log.Warning("⚠️ Could not determine endpoint name for {File}", file);
                            continue;
                        }

                        // Add the endpoint to the dictionary
                        endpoints[endpointName] = definition;
                        
                        Log.Information($"📊 SQL Endpoint: {endpointName}; Object: {definition.DatabaseSchema}.{definition.DatabaseObjectName}");
                    }
                    else
                    {
                        Log.Warning("⚠️ Failed to load SQL endpoint from {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Error parsing SQL endpoint file: {File}", file);
                }
            }

            Log.Information($"✅ Loaded {endpoints.Count} SQL endpoints from {endpointsDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error scanning SQL endpoints directory: {Directory}", endpointsDirectory);
        }

        return endpoints;
    }
    
    /// <summary>
    /// Parses a proxy endpoint definition from JSON, handling both legacy and extended formats
    /// </summary>
    private static EndpointDefinition? ParseProxyEndpointDefinition(string json)
    {
        try
        {
            // First try to parse as an ExtendedEndpointEntity (preferred format)
            var extendedEntity = JsonSerializer.Deserialize<ExtendedEndpointEntity>(json, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (extendedEntity != null && !string.IsNullOrWhiteSpace(extendedEntity.Url) && extendedEntity.Methods != null)
            {
                return new EndpointDefinition
                {
                    Url = extendedEntity.Url,
                    Methods = extendedEntity.Methods,
                    IsPrivate = extendedEntity.IsPrivate,
                    Type = ParseEndpointType(extendedEntity.Type),
                    CompositeConfig = extendedEntity.CompositeConfig
                };
            }
            
            // Try to parse as a standard EndpointEntity as fallback
            var entity = JsonSerializer.Deserialize<EndpointEntity>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (entity != null && !string.IsNullOrWhiteSpace(entity.Url) && entity.Methods != null)
            {
                return new EndpointDefinition
                {
                    Url = entity.Url,
                    Methods = entity.Methods,
                    IsPrivate = false, // Legacy format doesn't support IsPrivate
                    Type = EndpointType.Standard,
                    CompositeConfig = null
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error parsing proxy endpoint definition");
        }
        
        return null;
    }
    
    /// <summary>
    /// Parses a SQL endpoint definition from JSON
    /// </summary>
    private static EndpointDefinition? ParseSqlEndpointDefinition(string json)
    {
        try
        {
            // Parse as EndpointEntity
            var entity = JsonSerializer.Deserialize<EndpointEntity>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (entity != null && !string.IsNullOrWhiteSpace(entity.DatabaseObjectName))
            {
                var allowedMethods = entity.AllowedMethods ?? new List<string> { "GET" };
                var schema = entity.DatabaseSchema ?? "dbo";
                
                return new EndpointDefinition
                {
                    Type = EndpointType.SQL,
                    DatabaseObjectName = entity.DatabaseObjectName,
                    DatabaseSchema = schema,
                    AllowedColumns = entity.AllowedColumns ?? new List<string>(),
                    Procedure = entity.Procedure,
                    Methods = allowedMethods
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error parsing SQL endpoint definition");
        }
        
        return null;
    }
    
    /// <summary>
    /// Converts a string type to the EndpointType enum
    /// </summary>
    private static EndpointType ParseEndpointType(string? typeString)
    {
        if (string.IsNullOrWhiteSpace(typeString))
            return EndpointType.Standard;
            
        return typeString.ToLowerInvariant() switch
        {
            "composite" => EndpointType.Composite,
            "sql" => EndpointType.SQL,
            "private" => EndpointType.Private,
            _ => EndpointType.Standard
        };
    }
    
    /// <summary>
    /// Logs information about a loaded endpoint with appropriate emoji based on type
    /// </summary>
    private static void LogEndpointLoading(string endpointName, EndpointDefinition definition)
    {
        if (definition.IsPrivate)
        {
            Log.Debug("🔒 Loaded private endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
        else if (definition.IsComposite)
        {
            Log.Debug("🧩 Loaded composite endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
        else if (definition.IsSql)
        {
            Log.Debug("📊 Loaded SQL endpoint: {Name} -> {ObjectName}", endpointName, definition.DatabaseObjectName);
        }
        else
        {
            Log.Debug("♨️ Loaded standard endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
    }
    
    /// <summary>
    /// Creates sample endpoint definitions if none exist
    /// </summary>
    public static void CreateSampleEndpoints(string baseDirectory)
    {
        try
        {
            // Create SQL endpoint directory
            var sqlEndpointsDir = Path.Combine(baseDirectory, "SQL");
            if (!Directory.Exists(sqlEndpointsDir))
            {
                Directory.CreateDirectory(sqlEndpointsDir);
            }
            
            // Create SQL sample endpoint
            CreateSampleSqlEndpoint(sqlEndpointsDir);
            
            // Create Proxy endpoint directory
            var proxyEndpointsDir = Path.Combine(baseDirectory, "Proxy");
            if (!Directory.Exists(proxyEndpointsDir))
            {
                Directory.CreateDirectory(proxyEndpointsDir);
            }
            
            // Create Proxy sample endpoint
            CreateSampleProxyEndpoint(proxyEndpointsDir);
            
            // Create Composite sample endpoint
            CreateSampleCompositeEndpoint(proxyEndpointsDir);
            
            // Create Webhook directory
            var webhookDir = Path.Combine(baseDirectory, "Webhooks");
            if (!Directory.Exists(webhookDir))
            {
                Directory.CreateDirectory(webhookDir);
            }
            
            // Create Webhook sample endpoint
            CreateSampleWebhookEndpoint(webhookDir);
            
            // Clear the cached endpoints to force a reload
            lock (_loadLock)
            {
                _loadedProxyEndpoints = null;
                _loadedSqlEndpoints = null;
            }
            
            Log.Information("✅ Created sample endpoints in each endpoint directory");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error creating sample endpoint definitions");
        }
    }
    
    private static void CreateSampleSqlEndpoint(string sqlEndpointsDir)
    {
        var sampleDir = Path.Combine(sqlEndpointsDir, "Items");
        if (!Directory.Exists(sampleDir))
        {
            Directory.CreateDirectory(sampleDir);
        }

        var samplePath = Path.Combine(sampleDir, "entity.json");
        if (!File.Exists(samplePath))
        {
            var sample = new EndpointEntity
            {
                DatabaseObjectName = "Items",
                DatabaseSchema = "dbo",
                AllowedColumns = new List<string> { "ItemCode", "Description", "Price" },
                AllowedMethods = new List<string> { "GET" }
            };

            var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(samplePath, json);
            Log.Information($"✅ Created sample SQL endpoint definition: {samplePath}");
        }
    }
    
    private static void CreateSampleProxyEndpoint(string proxyEndpointsDir)
    {
        var sampleDir = Path.Combine(proxyEndpointsDir, "Sample");
        if (!Directory.Exists(sampleDir))
        {
            Directory.CreateDirectory(sampleDir);
        }

        var samplePath = Path.Combine(sampleDir, "entity.json");
        if (!File.Exists(samplePath))
        {
            var sample = new ExtendedEndpointEntity
            {
                Url = "https://jsonplaceholder.typicode.com/posts",
                Methods = new List<string> { "GET", "POST" },
                Type = "Standard",
                IsPrivate = false
            };

            var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(samplePath, json);
            Log.Information($"✅ Created sample proxy endpoint definition: {samplePath}");
        }
    }
    
    private static void CreateSampleCompositeEndpoint(string proxyEndpointsDir)
    {
        var compositeSampleDir = Path.Combine(proxyEndpointsDir, "SampleComposite");
        if (!Directory.Exists(compositeSampleDir))
        {
            Directory.CreateDirectory(compositeSampleDir);
        }
        
        var compositeSamplePath = Path.Combine(compositeSampleDir, "entity.json");
        if (!File.Exists(compositeSamplePath))
        {
            var compositeSample = new ExtendedEndpointEntity
            {
                Url = "http://localhost:8020/services/Exact.Entity.REST.EG",
                Methods = new List<string> { "POST" },
                Type = "Composite",
                CompositeConfig = new CompositeDefinition
                {
                    Name = "SampleComposite",
                    Description = "Sample composite endpoint",
                    Steps = new List<CompositeStep>
                    {
                        new CompositeStep
                        {
                            Name = "Step1",
                            Endpoint = "SampleEndpoint1",
                            Method = "POST",
                            TemplateTransformations = new Dictionary<string, string>
                            {
                                { "TransactionKey", "$guid" }
                            }
                        },
                        new CompositeStep
                        {
                            Name = "Step2",
                            Endpoint = "SampleEndpoint2",
                            Method = "POST",
                            DependsOn = "Step1",
                            TemplateTransformations = new Dictionary<string, string>
                            {
                                { "TransactionKey", "$prev.Step1.TransactionKey" }
                            }
                        }
                    }
                }
            };
            
            var json = JsonSerializer.Serialize(compositeSample, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(compositeSamplePath, json);
            Log.Information($"✅ Created sample composite endpoint definition: {compositeSamplePath}");
        }
    }
    
    private static void CreateSampleWebhookEndpoint(string webhookDir)
    {
        var samplePath = Path.Combine(webhookDir, "entity.json");
        if (!File.Exists(samplePath))
        {
            var sample = new EndpointEntity
            {
                DatabaseObjectName = "WebhookData",
                DatabaseSchema = "dbo",
                AllowedColumns = new List<string> { "webhook1", "webhook2" }
            };

            var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(samplePath, json);
            Log.Information($"✅ Created sample webhook endpoint definition: {samplePath}");
        }
    }
}