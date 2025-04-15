using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PortwayApi.Classes;

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "PortwayAPI";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "Comprehensive API for data integration and management";
    public ContactInfo Contact { get; set; } = new ContactInfo();
    public SecurityDefinitionInfo SecurityDefinition { get; set; } = new SecurityDefinitionInfo();
    public string RoutePrefix { get; set; } = "swagger";
    public string DocExpansion { get; set; } = "List";
    public int DefaultModelsExpandDepth { get; set; } = -1;
    public bool DisplayRequestDuration { get; set; } = true;
    public bool EnableFilter { get; set; } = true;
    public bool EnableDeepLinking { get; set; } = true;
    public bool EnableValidator { get; set; } = true;
}

public class ContactInfo
{
    public string Name { get; set; } = "PortwayAPI Support";
    public string Email { get; set; } = "support@portwayapi.com";
}

public class SecurityDefinitionInfo
{
    public string Name { get; set; } = "Bearer";
    public string Description { get; set; } = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"";
    public string In { get; set; } = "Header";
    public string Type { get; set; } = "ApiKey";
    public string Scheme { get; set; } = "Bearer";
}

public static class SwaggerConfiguration
{
    public static SwaggerSettings ConfigureSwagger(WebApplicationBuilder builder)
    {
        // Create default settings
        var swaggerSettings = new SwaggerSettings();
        
        try
        {
            // Attempt to bind from configuration
            var section = builder.Configuration.GetSection("Swagger");
            if (section.Exists())
            {
                section.Bind(swaggerSettings);
                Log.Information("✅ Swagger configuration loaded from appsettings.json");
            }
            else
            {
                Log.Warning("⚠️ No 'Swagger' section found in configuration. Using default settings.");
            }
        }
        catch (Exception ex)
        {
            // Log error but continue with defaults
            Log.Error(ex, "❌ Error loading Swagger configuration. Using default settings.");
        }
        
        // Ensure object references aren't null (defensive programming)
        swaggerSettings.Contact ??= new ContactInfo();
        swaggerSettings.SecurityDefinition ??= new SecurityDefinitionInfo();
        
        // Validate and fix critical values
        if (string.IsNullOrWhiteSpace(swaggerSettings.Title))
            swaggerSettings.Title = "PortwayAPI";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.Version))
            swaggerSettings.Version = "v1";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Name))
            swaggerSettings.SecurityDefinition.Name = "Bearer";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Scheme))
            swaggerSettings.SecurityDefinition.Scheme = "Bearer";
            
        // Register Swagger services if enabled
        if (swaggerSettings.Enabled)
        {
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo
                {
                    Title = swaggerSettings.Title,
                    Version = swaggerSettings.Version,
                    Description = swaggerSettings.Description ?? "API Documentation",
                    Contact = new OpenApiContact
                    {
                        Name = swaggerSettings.Contact.Name,
                        Email = swaggerSettings.Contact.Email
                    }
                });
                
                // Add security definition for Bearer token
                c.AddSecurityDefinition(swaggerSettings.SecurityDefinition.Name, new OpenApiSecurityScheme
                {
                    Description = swaggerSettings.SecurityDefinition.Description,
                    Name = "Authorization",
                    In = ParseEnum<ParameterLocation>(swaggerSettings.SecurityDefinition.In, ParameterLocation.Header),
                    Type = ParseEnum<SecuritySchemeType>(swaggerSettings.SecurityDefinition.Type, SecuritySchemeType.ApiKey),
                    Scheme = swaggerSettings.SecurityDefinition.Scheme
                });
                
                // Add security requirement
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = swaggerSettings.SecurityDefinition.Name
                            }
                        },
                        new string[] { }
                    }
                });
                
                // Add filters
                c.DocumentFilter<DynamicEndpointDocumentFilter>();
                c.OperationFilter<DynamicEndpointOperationFilter>();
            });

            builder.Services.AddSingleton<DynamicEndpointDocumentFilter>();
            builder.Services.AddSingleton<DynamicEndpointOperationFilter>();
            
            Log.Information("✅ Swagger services registered successfully");
        }
        else
        {
            Log.Information("ℹ️ Swagger is disabled in configuration");
        }
        
        return swaggerSettings;
    }

    public static void ConfigureSwaggerUI(WebApplication app, SwaggerSettings swaggerSettings)
    {
        if (!swaggerSettings.Enabled)
            return;
            
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint($"/swagger/{swaggerSettings.Version}/swagger.json", $"{swaggerSettings.Title} {swaggerSettings.Version}");
            c.RoutePrefix = swaggerSettings.RoutePrefix ?? "swagger";
            
            try
            {
                // Set doc expansion with fallback
                var docExpansion = ParseEnum<Swashbuckle.AspNetCore.SwaggerUI.DocExpansion>(
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
            }
            catch (Exception ex)
            {
                // Log but don't crash if there's an issue with UI configuration
                Log.Warning(ex, "⚠️ Error applying Swagger UI settings. Using defaults.");
                
                // Apply sensible defaults
                c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
                c.DefaultModelsExpandDepth(-1);
                c.DisplayRequestDuration();
            }
        });
        
        Log.Information("✅ Swagger UI configured successfully");
    }

    // Helper method for safely parsing enums with fallback
    public static T ParseEnum<T>(string value, T defaultValue) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<T>(value, true, out var result))
        {
            return defaultValue;
        }
        return result;
    }
}