using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Dapper;
using System.Text.Json;
using PortwayApi.Classes;
using PortwayApi.Interfaces;
using Serilog;
using System.Data;

namespace PortwayApi.Api;

[ApiController]
[Route("api/{env}/{endpointPath}")]
[ApiExplorerSettings(IgnoreApi = false)]
public class SQLAPI : ControllerBase
{
    private readonly IODataToSqlConverter _oDataToSqlConverter;
    private readonly IEnvironmentSettingsProvider _environmentSettingsProvider;

    public SQLAPI(
        IODataToSqlConverter oDataToSqlConverter, 
        IEnvironmentSettingsProvider environmentSettingsProvider)
    {
        _oDataToSqlConverter = oDataToSqlConverter;
        _environmentSettingsProvider = environmentSettingsProvider;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> QueryAsync(
        string env,
        string endpointPath,
        [FromQuery(Name = "$select")] string? select = null,
        [FromQuery(Name = "$filter")] string? filter = null,
        [FromQuery(Name = "$orderby")] string? orderby = null,
        [FromQuery(Name = "$top")] int top = 10,
        [FromQuery(Name = "$skip")] int skip = 0)
    {
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";
        Log.Information("📥 Request received: {Method} {Url}", Request.Method, url);

        try
        {
            // Check if this is a SQL endpoint - if not, return 404 to let the catchall handler take over
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration 
            var endpoint = sqlEndpoints[endpointPath];

            // Step 3: Extract endpoint details
            var schema = endpoint.DatabaseSchema ?? "dbo";
            var objectName = endpoint.DatabaseObjectName;
            var allowedColumns = endpoint.AllowedColumns ?? new List<string>();
            var allowedMethods = endpoint.Methods ?? new List<string> { "GET" };

            // Step 4: Check if GET is allowed
            if (!allowedMethods.Contains("GET"))
            {
                return StatusCode(405);
            }

            // Step 5: Validate column names
            if (allowedColumns.Count > 0)
            {
                // Validate select columns
                if (!string.IsNullOrEmpty(select))
                {
                    var selectedColumns = select.Split(',')
                        .Select(c => c.Trim())
                        .ToList();

                    var invalidColumns = selectedColumns
                        .Where(col => !allowedColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                        .ToList();

                    if (invalidColumns.Any())
                    {
                        return BadRequest(new { 
                            error = $"Selected columns not allowed: {string.Join(", ", invalidColumns)}", 
                            success = false 
                        });
                    }
                }
                else
                {
                    // If no select and columns are restricted, use allowed columns
                    select = string.Join(",", allowedColumns);
                }
            }

            // Step 6: Prepare OData parameters
            var odataParams = new Dictionary<string, string>
            {
                { "top", (top + 1).ToString() },
                { "skip", skip.ToString() }
            };

            if (!string.IsNullOrEmpty(select)) 
                odataParams["select"] = select;
            if (!string.IsNullOrEmpty(filter)) 
                odataParams["filter"] = filter;
            if (!string.IsNullOrEmpty(orderby)) 
                odataParams["orderby"] = orderby;

            // Step 7: Convert OData to SQL
            var (query, parameters) = _oDataToSqlConverter.ConvertToSQL($"{schema}.{objectName}", odataParams);

            // Step 8: Execute query
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var results = await connection.QueryAsync(query, parameters);
            var resultList = results.ToList();

            // Determine if it's the last page
            bool isLastPage = resultList.Count <= top;
            if (!isLastPage)
            {
                // Remove the extra row used for pagination
                resultList.RemoveAt(resultList.Count - 1);
            }

            // Step 9: Prepare response
            var response = new
            {
                Count = resultList.Count,
                Value = resultList,
                NextLink = isLastPage 
                    ? null 
                    : BuildNextLink(env, endpointPath, select, filter, orderby, top, skip)
            };

            Log.Information("✅ Successfully processed query for {Endpoint}", endpointPath);
            return Ok(response);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing SQL query for {Endpoint}", endpointPath);
            return Problem(
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> InsertAsync(
        string env,
        string endpointPath,
        [FromBody] JsonElement data)
    {
        try
        {
            // Check if this is a SQL endpoint - if not, return 404 to let the catchall handler take over
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration
            var endpoint = sqlEndpoints[endpointPath];

            // Step 3: Check if the endpoint supports POST and has a procedure defined
            if (!(endpoint.Methods?.Contains("POST") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support insert operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "INSERT");
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 5: Extract and add data parameters from the request
            foreach (var property in data.EnumerateObject())
            {
                dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
            }

            // Step 6: Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name properly
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            // Convert result to a list (could be empty if no rows returned)
            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed INSERT procedure for {Endpoint}", endpointPath);
            
            // Return the results, which typically includes the newly created ID
            return Ok(new { 
                success = true,
                message = "Record created successfully", 
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing INSERT for {Endpoint}", endpointPath);
            return Problem(
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpPut]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> UpdateAsync(
        string env,
        string endpointPath,
        [FromBody] JsonElement data)
    {
        try
        {
            // Check if this is a SQL endpoint - if not, return 404 to let the catchall handler take over
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration
            var endpoint = sqlEndpoints[endpointPath];

            // Step 3: Check if the endpoint supports PUT and has a procedure defined
            if (!(endpoint.Methods?.Contains("PUT") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support update operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Check if the ID is provided
            if (!data.TryGetProperty("id", out var idElement) && 
                !data.TryGetProperty("Id", out idElement) &&
                !data.TryGetProperty("ID", out idElement))
            {
                return BadRequest(new { 
                    error = "ID property is required for update operations", 
                    success = false 
                });
            }

            // Step 5: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "UPDATE");
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 6: Extract and add data parameters from the request
            foreach (var property in data.EnumerateObject())
            {
                dynamicParams.Add($"@{property.Name}", GetParameterValue(property.Value));
            }

            // Step 7: Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name properly
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            // Convert result to a list (could be empty if no rows returned)
            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed UPDATE procedure for {Endpoint}", endpointPath);
            
            // Return the results, which typically includes the updated record
            return Ok(new { 
                success = true,
                message = "Record updated successfully", 
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing UPDATE for {Endpoint}", endpointPath);
            return Problem(
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteAsync(
        string env,
        string endpointPath,
        [FromQuery] string id)
    {
        try
        {
            // Check if this is a SQL endpoint - if not, return 404 to let the catchall handler take over
            var sqlEndpoints = EndpointHandler.GetSqlEndpoints();
            if (!sqlEndpoints.ContainsKey(endpointPath))
            {
                return NotFound();
            }

            // Step 1: Validate environment
            var (connectionString, serverName) = await _environmentSettingsProvider.LoadEnvironmentOrThrowAsync(env);
            if (string.IsNullOrEmpty(connectionString))
            {
                return BadRequest(new { 
                    error = $"Invalid or missing environment: {env}", 
                    success = false 
                });
            }

            // Step 2: Get endpoint configuration
            var endpoint = sqlEndpoints[endpointPath];

            // Step 3: Check if the endpoint supports DELETE and has a procedure defined
            if (!(endpoint.Methods?.Contains("DELETE") ?? false))
            {
                return StatusCode(405, new { 
                    error = "Method not allowed",
                    success = false
                });
            }

            if (string.IsNullOrEmpty(endpoint.Procedure))
            {
                return BadRequest(new { 
                    error = "This endpoint does not support delete operations (no procedure defined)", 
                    success = false 
                });
            }

            // Step 4: Check if the ID is provided
            if (string.IsNullOrEmpty(id))
            {
                return BadRequest(new { 
                    error = "ID parameter is required for delete operations", 
                    success = false 
                });
            }

            // Step 5: Prepare stored procedure parameters
            var dynamicParams = new DynamicParameters();
            
            // Add method parameter (always needed for the standard procedure pattern)
            dynamicParams.Add("@Method", "DELETE");
            dynamicParams.Add("@id", id);
            
            // Add user parameter if available
            if (User.Identity?.Name != null)
            {
                dynamicParams.Add("@UserName", User.Identity.Name);
            }

            // Step 6: Execute stored procedure
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            
            // Parse procedure name properly
            string schema = "dbo";
            string procedureName = endpoint.Procedure;
            
            if (endpoint.Procedure.Contains("."))
            {
                var parts = endpoint.Procedure.Split('.');
                schema = parts[0].Trim('[', ']');
                procedureName = parts[1].Trim('[', ']');
            }

            var result = await connection.QueryAsync(
                $"[{schema}].[{procedureName}]", 
                dynamicParams, 
                commandType: CommandType.StoredProcedure
            );

            // Convert result to a list (could be empty if no rows returned)
            var resultList = result.ToList();
            
            Log.Information("✅ Successfully executed DELETE procedure for {Endpoint}", endpointPath);
            
            // Return the results, which typically includes deletion confirmation
            return Ok(new { 
                success = true,
                message = "Record deleted successfully", 
                id = id,
                result = resultList.FirstOrDefault() 
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing DELETE for {Endpoint}", endpointPath);
            return Problem(
                detail: ex.Message, 
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }

    // Helper method to build next link for pagination
    private string BuildNextLink(
        string env, 
        string endpointPath, 
        string? select, 
        string? filter, 
        string? orderby, 
        int top, 
        int skip)
    {
        var nextLink = $"/api/{env}/{endpointPath}?$top={top}&$skip={skip + top}";

        if (!string.IsNullOrWhiteSpace(select))
            nextLink += $"&$select={Uri.EscapeDataString(select)}";
        
        if (!string.IsNullOrWhiteSpace(filter))
            nextLink += $"&$filter={Uri.EscapeDataString(filter)}";
        
        if (!string.IsNullOrWhiteSpace(orderby))
            nextLink += $"&$orderby={Uri.EscapeDataString(orderby)}";

        return nextLink;
    }
    
    // Helper method to convert JsonElement to appropriate parameter value
    private static object? GetParameterValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt32(out int intValue) ? intValue 
                : element.TryGetDouble(out double doubleValue) ? doubleValue 
                : (object?)null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.ToString()
        };
    }
}