using Microsoft.AspNetCore.Mvc;
using Serilog;

namespace PortwayApi.Api;

/// <summary>
/// Controller to provide Swagger metadata only - not for actual request handling
/// This resolves the Swagger documentation issue while the real requests are handled by EndpointController
/// </summary>
[ApiController]
[Route("api/swagger-docs")]
[ApiExplorerSettings(IgnoreApi = true)]
public class SwaggerDocsController : ControllerBase
{
    /// <summary>
    /// This controller doesn't actually handle requests - it only exists to provide 
    /// properly formatted Swagger metadata for the API explorer.
    /// 
    /// The real implementation is in EndpointController, but this helps Swagger 
    /// know about the API structure.
    /// </summary>
    [HttpGet]
    [Route("info")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public IActionResult GetApiInfo()
    {
        // Just return the basic info - this endpoint is only for Swagger docs
        return Ok(new { 
            message = "This is a documentation-only endpoint. Use the actual endpoints for API calls.",
            endpoints = new {
                sql = "/api/{env}/{endpointName}",
                proxy = "/api/{env}/{endpointName}",
                webhook = "/webhook/{env}/{webhookId}",
                composite = "/api/{env}/composite/{endpointName}"
            }
        });
    }
}