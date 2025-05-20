using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using PortwayApi.Classes;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace PortwayApi.Classes.Swagger;

public class FileEndpointDocumentFilter : IDocumentFilter
{
    private readonly ILogger<FileEndpointDocumentFilter> _logger;

    public FileEndpointDocumentFilter(ILogger<FileEndpointDocumentFilter> logger)
    {
        _logger = logger;
    }

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        try
        {
            // Load file endpoint definitions
            var fileEndpoints = EndpointHandler.GetFileEndpoints();
            
            // Get allowed environments for parameter description
            var allowedEnvironments = GetAllowedEnvironments();

            // Add schema definitions for file models
            AddFileSchemas(swaggerDoc);

            // Create paths for each file endpoint
            foreach (var (endpointName, endpoint) in fileEndpoints)
            {
                if (endpoint.IsPrivate)
                {
                    continue; // Skip private endpoints
                }

                // Add file upload operation
                AddFileUploadOperation(swaggerDoc, endpointName, endpoint, allowedEnvironments);
                
                // Add file download operation
                AddFileDownloadOperation(swaggerDoc, endpointName, endpoint, allowedEnvironments);
                
                // Add file delete operation
                AddFileDeleteOperation(swaggerDoc, endpointName, endpoint, allowedEnvironments);
                
                // Add file listing operation
                AddFileListOperation(swaggerDoc, endpointName, endpoint, allowedEnvironments);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Swagger documentation for file endpoints");
        }
    }

    private void AddFileSchemas(OpenApiDocument swaggerDoc)
    {
        // Ensure components is initialized
        swaggerDoc.Components = swaggerDoc.Components ?? new OpenApiComponents();
        swaggerDoc.Components.Schemas = swaggerDoc.Components.Schemas ?? new Dictionary<string, OpenApiSchema>();

        // Add FileInfo schema
        swaggerDoc.Components.Schemas["FileInfo"] = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["fileId"] = new OpenApiSchema { Type = "string" },
                ["fileName"] = new OpenApiSchema { Type = "string" },
                ["contentType"] = new OpenApiSchema { Type = "string" },
                ["size"] = new OpenApiSchema { Type = "integer", Format = "int64" },
                ["lastModified"] = new OpenApiSchema { Type = "string", Format = "date-time" },
                ["environment"] = new OpenApiSchema { Type = "string" },
                ["isInMemoryOnly"] = new OpenApiSchema { Type = "boolean" }
            },
            Required = new HashSet<string> { "fileId", "fileName", "contentType" }
        };

        // Add FileUploadResponse schema
        swaggerDoc.Components.Schemas["FileUploadResponse"] = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["success"] = new OpenApiSchema { Type = "boolean" },
                ["fileId"] = new OpenApiSchema { Type = "string" },
                ["filename"] = new OpenApiSchema { Type = "string" },
                ["contentType"] = new OpenApiSchema { Type = "string" },
                ["size"] = new OpenApiSchema { Type = "integer", Format = "int64" },
                ["url"] = new OpenApiSchema { Type = "string" }
            },
            Required = new HashSet<string> { "success", "fileId", "filename" }
        };

        // Add FileListResponse schema
        swaggerDoc.Components.Schemas["FileListResponse"] = new OpenApiSchema
        {
            Type = "object",
            Properties = new Dictionary<string, OpenApiSchema>
            {
                ["success"] = new OpenApiSchema { Type = "boolean" },
                ["files"] = new OpenApiSchema
                {
                    Type = "array",
                    Items = new OpenApiSchema { Reference = new OpenApiReference { Type = ReferenceType.Schema, Id = "FileInfo" } }
                },
                ["count"] = new OpenApiSchema { Type = "integer" }
            },
            Required = new HashSet<string> { "success", "files", "count" }
        };
    }

    private void AddFileUploadOperation(OpenApiDocument swaggerDoc, string endpointName, EndpointDefinition endpoint, List<string> allowedEnvironments)
    {
        // Path for upload: /api/{env}/files/{endpointName}
        string path = $"/api/{{env}}/files/{endpointName}";
        
        if (!swaggerDoc.Paths.ContainsKey(path))
        {
            swaggerDoc.Paths.Add(path, new OpenApiPathItem());
        }

        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Files" } },
            Summary = $"Upload file to {endpointName}",
            Description = $"Uploads a file to the {endpointName} storage endpoint",
            OperationId = $"uploadFile_{endpointName}".Replace(" ", "_"),
            Parameters = new List<OpenApiParameter>()
        };

        // Environment parameter
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

        // Overwrite parameter
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "overwrite",
            In = ParameterLocation.Query,
            Required = false,
            Schema = new OpenApiSchema { Type = "boolean", Default = new OpenApiBoolean(false) },
            Description = "Set to true to overwrite existing files with the same name"
        });

        // File parameter
        operation.RequestBody = new OpenApiRequestBody
        {
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = "object",
                        Properties = new Dictionary<string, OpenApiSchema>
                        {
                            ["file"] = new OpenApiSchema
                            {
                                Type = "string",
                                Format = "binary",
                                Description = "The file to upload"
                            }
                        },
                        Required = new HashSet<string> { "file" }
                    }
                }
            }
        };

        // Success response
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "File uploaded successfully",
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
                                ["fileId"] = new OpenApiSchema { Type = "string" },
                                ["filename"] = new OpenApiSchema { Type = "string" },
                                ["contentType"] = new OpenApiSchema { Type = "string" },
                                ["size"] = new OpenApiSchema { Type = "integer", Format = "int64" },
                                ["url"] = new OpenApiSchema { Type = "string" }
                            }
                        }
                    }
                }
            },
            ["400"] = new OpenApiResponse { Description = "Bad request - invalid file or request" },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["403"] = new OpenApiResponse { Description = "Forbidden - file type not allowed" },
            ["413"] = new OpenApiResponse { Description = "Payload Too Large - file exceeds size limit" }
        };

        // Add file endpoint properties info
        AddFileEndpointPropertiesInfo(operation, endpoint, "upload");
        
        // Add curl examples
        AddCurlExamples(operation, "upload", endpointName);
        
        // Add the upload operation
        swaggerDoc.Paths[path].Operations[OperationType.Post] = operation;
    }

    private void AddFileDownloadOperation(OpenApiDocument swaggerDoc, string endpointName, EndpointDefinition endpoint, List<string> allowedEnvironments)
    {
        // Path for download: /api/{env}/files/{endpointName}/{fileId}
        string path = $"/api/{{env}}/files/{endpointName}/{{fileId}}";
        
        if (!swaggerDoc.Paths.ContainsKey(path))
        {
            swaggerDoc.Paths.Add(path, new OpenApiPathItem());
        }

        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Files" } },
            Summary = $"Download file from {endpointName}",
            Description = $"Downloads a file from the {endpointName} storage endpoint",
            OperationId = $"downloadFile_{endpointName}".Replace(" ", "_"),
            Parameters = new List<OpenApiParameter>()
        };

        // Environment parameter
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

        // File ID parameter
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "fileId",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = "string" },
            Description = "ID of the file to download"
        });

        // Success response
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "File content",
                Content = new Dictionary<string, OpenApiMediaType>
                {
                    ["*/*"] = new OpenApiMediaType
                    {
                        Schema = new OpenApiSchema
                        {
                            Type = "string",
                            Format = "binary"
                        }
                    }
                }
            },
            ["400"] = new OpenApiResponse { Description = "Bad request - invalid file ID" },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["404"] = new OpenApiResponse { Description = "File not found" }
        };

        // Add file endpoint properties info
        AddFileEndpointPropertiesInfo(operation, endpoint, "download");
        
        // Add curl examples
        AddCurlExamples(operation, "download", endpointName);

        // Add the download operation
        swaggerDoc.Paths[path].Operations[OperationType.Get] = operation;
    }

    private void AddFileDeleteOperation(OpenApiDocument swaggerDoc, string endpointName, EndpointDefinition endpoint, List<string> allowedEnvironments)
    {
        // Path for delete: /api/{env}/files/{endpointName}/{fileId}
        string path = $"/api/{{env}}/files/{endpointName}/{{fileId}}";
        
        if (!swaggerDoc.Paths.ContainsKey(path))
        {
            swaggerDoc.Paths.Add(path, new OpenApiPathItem());
        }

        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Files" } },
            Summary = $"Delete file from {endpointName}",
            Description = $"Deletes a file from the {endpointName} storage endpoint",
            OperationId = $"deleteFile_{endpointName}".Replace(" ", "_"),
            Parameters = new List<OpenApiParameter>()
        };

        // Environment parameter
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

        // File ID parameter
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "fileId",
            In = ParameterLocation.Path,
            Required = true,
            Schema = new OpenApiSchema { Type = "string" },
            Description = "ID of the file to delete"
        });

        // Success response
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "File deleted successfully",
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
                                ["message"] = new OpenApiSchema { Type = "string" }
                            }
                        }
                    }
                }
            },
            ["400"] = new OpenApiResponse { Description = "Bad request - invalid file ID" },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["404"] = new OpenApiResponse { Description = "File not found" }
        };

        // Add examples
        AddExamples(operation, "delete", endpointName);
        
        // Add file endpoint properties info
        AddFileEndpointPropertiesInfo(operation, endpoint, "delete");
        
        // Add curl examples
        AddCurlExamples(operation, "delete", endpointName);

        // Add the delete operation
        swaggerDoc.Paths[path].Operations[OperationType.Delete] = operation;
    }

    private void AddFileListOperation(OpenApiDocument swaggerDoc, string endpointName, EndpointDefinition endpoint, List<string> allowedEnvironments)
    {
        // Path for listing: /api/{env}/files/{endpointName}/list
        string path = $"/api/{{env}}/files/{endpointName}/list";
        
        if (!swaggerDoc.Paths.ContainsKey(path))
        {
            swaggerDoc.Paths.Add(path, new OpenApiPathItem());
        }

        var operation = new OpenApiOperation
        {
            Tags = new List<OpenApiTag> { new OpenApiTag { Name = "Files" } },
            Summary = $"List files in {endpointName}",
            Description = $"Lists all files in the {endpointName} storage endpoint",
            OperationId = $"listFiles_{endpointName}".Replace(" ", "_"),
            Parameters = new List<OpenApiParameter>()
        };

        // Environment parameter
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

        // Prefix parameter
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "prefix",
            In = ParameterLocation.Query,
            Required = false,
            Schema = new OpenApiSchema { Type = "string" },
            Description = "Filter files by prefix"
        });

        // Success response
        operation.Responses = new OpenApiResponses
        {
            ["200"] = new OpenApiResponse
            {
                Description = "List of files",
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
                                ["files"] = new OpenApiSchema
                                {
                                    Type = "array",
                                    Items = new OpenApiSchema
                                    {
                                        Type = "object",
                                        Properties = new Dictionary<string, OpenApiSchema>
                                        {
                                            ["fileId"] = new OpenApiSchema { Type = "string" },
                                            ["fileName"] = new OpenApiSchema { Type = "string" },
                                            ["contentType"] = new OpenApiSchema { Type = "string" },
                                            ["size"] = new OpenApiSchema { Type = "integer", Format = "int64" },
                                            ["lastModified"] = new OpenApiSchema { Type = "string", Format = "date-time" },
                                            ["url"] = new OpenApiSchema { Type = "string" }
                                        }
                                    }
                                },
                                ["count"] = new OpenApiSchema { Type = "integer" }
                            }
                        }
                    }
                }
            },
            ["401"] = new OpenApiResponse { Description = "Unauthorized" },
            ["404"] = new OpenApiResponse { Description = "Endpoint not found" }
        };

        // Add examples
        AddExamples(operation, "list", endpointName);
        
        // Add file endpoint properties info
        AddFileEndpointPropertiesInfo(operation, endpoint, "list");
        
        // Add curl examples
        AddCurlExamples(operation, "list", endpointName);

        // Add the list operation
        swaggerDoc.Paths[path].Operations[OperationType.Get] = operation;
    }

    private void AddExamples(OpenApiOperation operation, string operationType, string endpointName)
    {
        if (operationType == "upload")
        {
            // No examples for multipart/form-data uploads
        }
        else if (operationType == "download")
        {
            // No examples for binary downloads
        }
        else if (operationType == "delete")
        {
            if (operation.Responses["200"].Content.ContainsKey("application/json"))
            {
                operation.Responses["200"].Content["application/json"].Examples = new Dictionary<string, OpenApiExample>
                {
                    ["success"] = new OpenApiExample
                    {
                        Value = new OpenApiObject
                        {
                            ["success"] = new OpenApiBoolean(true),
                            ["message"] = new OpenApiString("File deleted successfully")
                        },
                        Summary = "Successful deletion"
                    }
                };
            }
        }
        else if (operationType == "list")
        {
            if (operation.Responses["200"].Content.ContainsKey("application/json"))
            {
                operation.Responses["200"].Content["application/json"].Examples = new Dictionary<string, OpenApiExample>
                {
                    ["fileList"] = new OpenApiExample
                    {
                        Value = new OpenApiObject
                        {
                            ["success"] = new OpenApiBoolean(true),
                            ["files"] = new OpenApiArray
                            {
                                new OpenApiObject
                                {
                                    ["fileId"] = new OpenApiString("YTAwOmV4YW1wbGUucGRm"),
                                    ["fileName"] = new OpenApiString("example.pdf"),
                                    ["contentType"] = new OpenApiString("application/pdf"),
                                    ["size"] = new OpenApiInteger(12345),
                                    ["lastModified"] = new OpenApiString("2025-05-20T10:15:30Z"),
                                    ["url"] = new OpenApiString($"/api/600/files/{endpointName}/YTAwOmV4YW1wbGUucGRm")
                                },
                                new OpenApiObject
                                {
                                    ["fileId"] = new OpenApiString("YTAwOmltYWdlLmpwZw"),
                                    ["fileName"] = new OpenApiString("image.jpg"),
                                    ["contentType"] = new OpenApiString("image/jpeg"),
                                    ["size"] = new OpenApiInteger(54321),
                                    ["lastModified"] = new OpenApiString("2025-05-19T14:30:45Z"),
                                    ["url"] = new OpenApiString($"/api/600/files/{endpointName}/YTAwOmltYWdlLmpwZw")
                                }
                            },
                            ["count"] = new OpenApiInteger(2)
                        },
                        Summary = "File listing example"
                    }
                };
            }
        }
    }

    private void AddFileEndpointPropertiesInfo(OpenApiOperation operation, EndpointDefinition endpoint, string operationType)
    {
        // Add description about the file endpoint's restrictions
        var description = new StringBuilder(operation.Description ?? "");
        
        // Add base directory info if exists
        if (endpoint.Properties != null && endpoint.Properties.TryGetValue("BaseDirectory", out var baseDir) && 
            baseDir is string baseDirString && 
            !string.IsNullOrEmpty(baseDirString))
        {
            description.AppendLine($"\n\nFiles are stored in the '{baseDirString}' subdirectory within the environment.");
        }
        
        // Add allowed extensions info if exists
        if (endpoint.Properties != null && endpoint.Properties.TryGetValue("AllowedExtensions", out var extensions) && 
            extensions is List<string> allowedExtensions && 
            allowedExtensions.Count > 0)
        {
            description.AppendLine($"\n\nAllowed file extensions: {string.Join(", ", allowedExtensions)}");
        }
        
        // Add storage type info
        if (endpoint.Properties != null && endpoint.Properties.TryGetValue("StorageType", out var storageType) && 
            storageType is string storageTypeString)
        {
            description.AppendLine($"\n\nStorage type: {storageTypeString}");
        }
        
        // Update operation description
        operation.Description = description.ToString();
    }

    private void AddCurlExamples(OpenApiOperation operation, string operationType, string endpointName)
    {
        var curlExample = new StringBuilder();
        
        if (operationType == "upload")
        {
            curlExample.AppendLine("```bash");
            curlExample.AppendLine($"curl -X POST \"https://yourserver.com/api/600/files/{endpointName}\"");
            curlExample.AppendLine("  -H \"Authorization: Bearer YOUR_TOKEN\"");
            curlExample.AppendLine("  -H \"Content-Type: multipart/form-data\"");
            curlExample.AppendLine("  -F \"file=@/path/to/yourfile.pdf\"");
            curlExample.AppendLine("```");
        }
        else if (operationType == "download")
        {
            curlExample.AppendLine("```bash");
            curlExample.AppendLine($"curl -X GET \"https://yourserver.com/api/600/files/{endpointName}/YOUR_FILE_ID\"");
            curlExample.AppendLine("  -H \"Authorization: Bearer YOUR_TOKEN\"");
            curlExample.AppendLine("  --output downloaded_file.pdf");
            curlExample.AppendLine("```");
        }
        else if (operationType == "delete")
        {
            curlExample.AppendLine("```bash");
            curlExample.AppendLine($"curl -X DELETE \"https://yourserver.com/api/600/files/{endpointName}/YOUR_FILE_ID\"");
            curlExample.AppendLine("  -H \"Authorization: Bearer YOUR_TOKEN\"");
            curlExample.AppendLine("```");
        }
        else if (operationType == "list")
        {
            curlExample.AppendLine("```bash");
            curlExample.AppendLine($"curl -X GET \"https://yourserver.com/api/600/files/{endpointName}/list\"");
            curlExample.AppendLine("  -H \"Authorization: Bearer YOUR_TOKEN\"");
            curlExample.AppendLine("```");
        }
        
        // Add curl examples to description
        if (operation.Description == null)
        {
            operation.Description = curlExample.ToString();
        }
        else
        {
            operation.Description += "\n\n## Example\n" + curlExample.ToString();
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

    // Helper classes for deserializing settings.json
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
