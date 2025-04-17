using System.IO.Compression;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SqlKata.Compilers;
using PortwayApi.Api;
using PortwayApi.Auth;
using PortwayApi.Classes;
using PortwayApi.Endpoints;
using PortwayApi.Helpers;
using PortwayApi.Interfaces;
using PortwayApi.Middleware;
using PortwayApi.Services;
using System.Text;
using System.Text.Json;
using Microsoft.OpenApi.Models;

// Create log directory
Directory.CreateDirectory("log");

// Configure logger
Log.Logger = new LoggerConfiguration()
   .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
   .WriteTo.File(
       path: "log/portwayapi-.log",
       rollingInterval: RollingInterval.Day,
       fileSizeLimitBytes: 10 * 1024 * 1024,
       rollOnFileSizeLimit: true,
       retainedFileCountLimit: 10,
       restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
       buffered: true,
       flushToDiskInterval: TimeSpan.FromSeconds(30))
   .MinimumLevel.Information() // Change default from Debug to Information
   .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
   .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
   .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
   .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
   .Filter.ByExcluding(logEvent =>
       logEvent.Properties.ContainsKey("RequestPath") &&
       (logEvent.Properties["RequestPath"].ToString().Contains("/swagger") ||
        logEvent.Properties["RequestPath"].ToString().Contains("/index.html")))
   .CreateLogger();

LogApplicationStartup();
Log.Information("🔍 Logging initialized successfully");

try
{
    // Create WebApplication Builder
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    builder.Configuration.AddJsonFile("appsettings.json", optional: false)
                     .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true);

    // Configure Kestrel 
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        // 1. Disable server header (security)
        serverOptions.AddServerHeader = false;
        
        // 2. Set appropriate request limits
        serverOptions.Limits.MaxRequestBodySize = 10 * 1024 * 1024; // 10 MB request body limit
        serverOptions.Limits.MaxRequestHeadersTotalSize = 32 * 1024; // 32 KB for headers
        
        // 3. Configure timeouts for better client handling
        serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
        serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
        
        // 4. Connection rate limiting to prevent DoS
        serverOptions.Limits.MaxConcurrentConnections = 1000;
        serverOptions.Limits.MaxConcurrentUpgradedConnections = 100;
        
        // 5. Data rate limiting to prevent slow requests
        serverOptions.Limits.MinRequestBodyDataRate = new MinDataRate(
            bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
        serverOptions.Limits.MinResponseDataRate = new MinDataRate(
            bytesPerSecond: 100, gracePeriod: TimeSpan.FromSeconds(10));
        
        // 6. HTTP/2 specific settings
        serverOptions.Limits.Http2.MaxStreamsPerConnection = 100;
        serverOptions.Limits.Http2.MaxFrameSize = 16 * 1024; // 16 KB
        serverOptions.Limits.Http2.InitialConnectionWindowSize = 128 * 1024; // 128 KB
        serverOptions.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(30);
        serverOptions.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(60);

        // 7. Configure HTTPS by forcing SSL when it's not localhost
        if (!builder.Environment.IsDevelopment())
        {
            serverOptions.ConfigureEndpointDefaults(listenOptions =>
            {
                listenOptions.UseHttps();
            });
        }
    });

    // Add response compression
    builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });

    builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
    {
        options.Level = CompressionLevel.Fastest;
    });
    builder.Services.Configure<GzipCompressionProviderOptions>(options =>
    {
        options.Level = CompressionLevel.Fastest;
    });

    // Add services
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();
    
    // Add traffic logging
    builder.Services.AddProxyTrafficLogging(builder.Configuration);

    // Define server name
    string serverName = Environment.MachineName;

    // Configure logging
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.FormatterName = "simple");
    builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ");

    // Configure SQLite Authentication Database
    var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "auth.db");
    builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
    builder.Services.AddScoped<TokenService>();
    builder.Services.AddAuthorization();
    builder.Services.AddHostedService<LogFlusher>();

    // Register route constraint for ProxyConstraint
    builder.Services.Configure<RouteOptions>(options =>
    {
        options.ConstraintMap.Add("proxy", typeof(ProxyConstraintAttribute));
    });

    // Configure HTTP client
    builder.Services.AddHttpClient("ProxyClient")
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            return new HttpClientHandler
            {
                UseDefaultCredentials = true, // Uses Windows credentials of the application
                PreAuthenticate = true
            };
        });

    // Register environment settings providers
    builder.Services.AddSingleton<IEnvironmentSettingsProvider, EnvironmentSettingsProvider>();
    builder.Services.AddSingleton<EnvironmentSettings>();

    // Register OData SQL services
    builder.Services.AddSingleton<IHostedService, StartupLogger>();
    builder.Services.AddSingleton<IEdmModelBuilder, EdmModelBuilder>();
    builder.Services.AddSingleton<Compiler, SqlServerCompiler>();
    builder.Services.AddSingleton<IODataToSqlConverter, ODataToSqlConverter>();

    // Initialize endpoints directories
    EnsureDirectoryStructure();
    
    // Load Proxy endpoints
    var proxyEndpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints", "Proxy");
    var proxyEndpointMap = EndpointHandler.GetEndpoints(proxyEndpointsDirectory);

    // Register CompositeEndpointHandler with loaded endpoints
    builder.Services.AddSingleton<CompositeEndpointHandler>(provider => 
        new CompositeEndpointHandler(
            provider.GetRequiredService<IHttpClientFactory>(),
            proxyEndpointMap,
            serverName
        )
    );

    // Configure Rate Limiting
    builder.Services.AddRateLimiting(builder.Configuration);

    // Configure SSRF protection
    var urlValidatorPath = Path.Combine(Directory.GetCurrentDirectory(), "environments", "network-access-policy.json");
    if (!File.Exists(urlValidatorPath))
    {
        var directory = Path.GetDirectoryName(urlValidatorPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
        File.WriteAllText(urlValidatorPath, JsonSerializer.Serialize(new 
        { 
            allowedHosts = new[] { "localhost", "127.0.0.1" },
            blockedIpRanges = new[] 
            { 
                "10.0.0.0/8", 
                "172.16.0.0/12", 
                "192.168.0.0/16", 
                "169.254.0.0/16" 
            }
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    var urlValidator = new UrlValidator(urlValidatorPath);
    builder.Services.AddSingleton(urlValidator);

    // Configure Health Checks
    builder.Services.AddHealthChecks();
    
    // Register HealthCheckService wrapper with all dependencies
    builder.Services.AddSingleton<PortwayApi.Services.HealthCheckService>(sp => 
    {
        var healthCheckService = sp.GetRequiredService<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService>();
        var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
        var endpointMap = EndpointHandler.GetEndpoints(proxyEndpointsDirectory);
        
        return new PortwayApi.Services.HealthCheckService(
            healthCheckService, 
            TimeSpan.FromSeconds(30),
            httpClientFactory,
            endpointMap);
    });

    // Configure Swagger
    var swaggerSettings = SwaggerConfiguration.ConfigureSwagger(builder);
    builder.Services.AddSingleton<DynamicEndpointDocumentFilter>();
    builder.Services.AddSingleton<DynamicEndpointOperationFilter>();
    builder.Services.AddSingleton<CompositeEndpointDocumentFilter>();
    builder.Services.AddSingleton<ComplexParameterFilter>();
    builder.Services.AddSingleton<AlphabeticalEndpointSorter>();

    // Build the application
    var app = builder.Build();

    // Configure middleware pipeline
    app.UseResponseCompression();
    app.UseExceptionHandlingMiddleware();
    app.UseSecurityHeaders();
    app.UseProxyTrafficLogging();
    
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        DefaultFileNames = new List<string> { "index.html" }
    });
    app.UseStaticFiles();

    // Configure Swagger UI
    if(swaggerSettings.Enabled)
    {
        app.UseSwagger(options => {
            // This ensures you get detailed error information
            options.PreSerializeFilters.Add((swagger, httpReq) => {
                // Use the BaseProtocol setting if provided, otherwise use the request's scheme
                string scheme = swaggerSettings.BaseProtocol ?? httpReq.Scheme;
                
                // Force HTTPS in production or for public domains
                string host = httpReq.Host.Value;
                bool isProduction = !builder.Environment.IsDevelopment();
                
                if ((swaggerSettings.ForceHttpsInProduction && isProduction) || 
                    (host.Contains(".") && !host.Contains("localhost") && !host.Contains("127.0.0.1"))) {
                    scheme = "https";
                    Log.Information("🔒 Forcing HTTPS in Swagger documentation: Environment={Env}, Host={Host}", 
                        builder.Environment.EnvironmentName, host);
                }
                
                // Also check for standard HTTPS headers
                if (httpReq.Headers.ContainsKey("X-Forwarded-Proto") && 
                    httpReq.Headers["X-Forwarded-Proto"] == "https") {
                    scheme = "https";
                }
                
                swagger.Servers = new List<OpenApiServer>
                {
                    new OpenApiServer { Url = $"{scheme}://{host}{httpReq.PathBase}" }
                };
            });
        });

        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", $"{swaggerSettings.Title} {swaggerSettings.Version}");
            c.RoutePrefix = swaggerSettings.RoutePrefix ?? "swagger";
        
            var docExpansion = SwaggerConfiguration.ParseEnum<Swashbuckle.AspNetCore.SwaggerUI.DocExpansion>(
                swaggerSettings.DocExpansion, 
                Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
            c.DocExpansion(docExpansion);
            
            // Apply other settings
            c.DefaultModelsExpandDepth(swaggerSettings.DefaultModelsExpandDepth);
            
            if (swaggerSettings.DisplayRequestDuration)
                c.DisplayRequestDuration();
                
            if (swaggerSettings.EnableFilter)
                c.EnableFilter();
                
            if (swaggerSettings.EnableDeepLinking)
                c.EnableDeepLinking();
                
            if (swaggerSettings.EnableValidator)
                c.EnableValidator();
        });
    }

    // Initialize Database & Create Default Token if needed
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

        try
        {
            // Set up database and migrate if needed
            context.Database.EnsureCreated();
            context.EnsureTablesCreated();

            // Create a default token if none exist
            var activeTokens = await tokenService.GetActiveTokensAsync();
            if (!activeTokens.Any())
            {
                var token = await tokenService.GenerateTokenAsync(serverName);
                Log.Information("🗝️ Generated new default token for {ServerName}", serverName);
                Log.Information("📁 Token has been saved to tokens/{ServerName}.txt", serverName);
            }
            else
            {
                Log.Information("✅ Using existing tokens. Total active tokens: {Count}", activeTokens.Count());
                Log.Warning("📁 Tokens are available in the tokens directory.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("❌ Database initialization failed: {Message}", ex.Message);
        }
    }

    // Enable request/response logging
    app.Use(async (context, next) => {
        Log.Information("📥 Incoming request: {Method} {Path}", context.Request.Method, context.Request.Path);
        await next();
        Log.Information("📤 Outgoing response: {StatusCode} for {Path}", context.Response.StatusCode, context.Request.Path);
    });

    // Get environment settings services
    var environmentSettings = app.Services.GetRequiredService<EnvironmentSettings>();
    var sqlEnvironmentProvider = app.Services.GetRequiredService<IEnvironmentSettingsProvider>();

    // Log loaded proxy endpoints
    foreach (var entry in proxyEndpointMap)
    {
        string endpointName = entry.Key;
        var (url, methods, isPrivate, type) = entry.Value;
        
        if (isPrivate)
        {
            Log.Information($"🔒 Private Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
        else if (type.Equals("Composite", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"🧩 Composite Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
        else
        {
            Log.Information($"✅ Proxy Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
    }

    // Log loaded SQL endpoints
    var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
    foreach (var endpoint in sqlEndpoints)
    {
        Log.Information($"📊 SQL Endpoint: {endpoint.Key}; Object: {endpoint.Value.DatabaseSchema}.{endpoint.Value.DatabaseObjectName}");
    }

    // Log Loaded Webhook endpoint
    var webhookEndpoints = EndpointHandler.GetSqlWebhookEndpoints();
    foreach (var endpoint in webhookEndpoints)
    {
        Log.Information($"🔔 Webhook Endpoint: {endpoint.Key} available");
    }

    // Use Rate Limiting middleware
    PortwayApi.Middleware.RateLimiterExtensions.UseRateLimiter(app);

    // Use Token Authentication middleware
    app.UseTokenAuthentication();
    app.UseAuthorization();

    var forwardedHeadersOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | 
                        ForwardedHeaders.XForwardedProto | 
                        ForwardedHeaders.XForwardedHost,
        RequireHeaderSymmetry = false,
        ForwardLimit = null 
    };
    forwardedHeadersOptions.KnownNetworks.Clear();
    forwardedHeadersOptions.KnownProxies.Clear();

    app.UseForwardedHeaders(forwardedHeadersOptions);

    app.Use((context, next) =>
    {
        // Check for Cloudflare headers
        if (context.Request.Headers.TryGetValue("CF-Visitor", out var cfVisitor))
        {
            if (cfVisitor.ToString().Contains("\"scheme\":\"https\""))
            {
                context.Request.Scheme = "https";
            }
        }
        
        // Also check for Cloudflare connecting protocol
        if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var _))
        {
            // We're behind Cloudflare, so trust the X-Forwarded-Proto header
            if (context.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) && 
                proto == "https")
            {
                context.Request.Scheme = "https";
            }
        }
        
        return next();
    });

    // Map controller routes
    app.MapControllers();

    // Register Composite middleware
    app.MapCompositeEndpoint();
  
    // Map health check endpoints
    PortwayApi.Endpoints.HealthCheckEndpointExtensions.MapHealthCheckEndpoints(app);

    // Log application URLs
    var urls = app.Urls;
    if (urls != null && urls.Any())
    {
        Log.Information("🌐 Application is hosted on the following URLs:");
        foreach (var url in urls)
        {
            Log.Information("   {Url}", url);
        }
    }
    else if (builder.Environment.IsProduction() && Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH") != null)
    {
        // We're running in IIS
        Log.Information("🌐 Application is hosted in IIS");
    }
    else
    {
        var serverUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") 
            ?? builder.Configuration["Kestrel:Endpoints:Http:Url"] 
            ?? builder.Configuration["urls"]
            ?? "http://localhost:5000";
        
        Log.Information("🌐 Application is hosted on: {Urls}", serverUrls);
    }

    // Register application shutdown handler
    app.Lifetime.ApplicationStopping.Register(() => 
    {
        Log.Information("Application shutting down...");
        Log.CloseAndFlush();
    });

    // Run the application
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Application failed to start.");
}
finally
{
    Log.CloseAndFlush();
}

// Helper function to ensure directory structure
void EnsureDirectoryStructure()
{
    // Ensure base endpoints directory exists
    var endpointsBaseDir = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
    if (!Directory.Exists(endpointsBaseDir))
        Directory.CreateDirectory(endpointsBaseDir);
   
    // Ensure SQL endpoints directory exists
    var sqlEndpointsDir = Path.Combine(endpointsBaseDir, "SQL");
    if (!Directory.Exists(sqlEndpointsDir))
        Directory.CreateDirectory(sqlEndpointsDir);
   
    // Ensure Proxy endpoints directory exists
    var proxyEndpointsDir = Path.Combine(endpointsBaseDir, "Proxy");
    if (!Directory.Exists(proxyEndpointsDir))
        Directory.CreateDirectory(proxyEndpointsDir);
   
    // Ensure Webhook directory exists
    var webhookDir = Path.Combine(endpointsBaseDir, "Webhooks");
    if (!Directory.Exists(webhookDir))
        Directory.CreateDirectory(webhookDir);
}

void LogApplicationStartup()
{
    var logo = new StringBuilder();
    logo.AppendLine(@"");
    logo.AppendLine(@"  _____           _                        ");
    logo.AppendLine(@" |  __ \         | |                       ");
    logo.AppendLine(@" | |__) |__  _ __| |___      ____ _ _   _  ");
    logo.AppendLine(@" |  ___/ _ \| '__| __\ \ /\ / / _` | | | | ");
    logo.AppendLine(@" | |  | (_) | |  | |_ \ V  V / (_| | |_| | ");
    logo.AppendLine(@" |_|   \___/|_|   \__| \_/\_/ \__,_|\__, | ");
    logo.AppendLine(@"                                      _/ | ");
    logo.AppendLine(@"                                     |___/ ");
    logo.AppendLine(@"");
    Log.Information(logo.ToString());
}

// Extension method to configure rate limiting services
public static class RateLimitingExtensions
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        // Nothing to configure, the middleware doesn't require additional services
        return services;
    }
}