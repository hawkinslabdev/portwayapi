using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PortwayApi.Classes;
using PortwayApi.Helpers;
using PortwayApi.Interfaces;
using PortwayApi.Services;
using System.Net.Http.Headers;
using System.Text;

namespace PortwayApi.Tests.Base
{
    public class ApiTestBase : IDisposable
    {
        protected readonly HttpClient _client;
        protected readonly Mock<IEnvironmentSettingsProvider> _mockEnvironmentSettingsProvider;
        protected readonly Mock<UrlValidator> _mockUrlValidator;
        protected readonly Mock<EnvironmentSettings> _mockEnvironmentSettings;
        protected readonly Mock<IODataToSqlConverter> _mockODataToSqlConverter;
        protected readonly Mock<SqlConnectionPoolService> _mockConnectionPoolService;
        protected readonly WebApplicationFactory<Program> _factory;

        public ApiTestBase()
        {
            _mockEnvironmentSettingsProvider = new Mock<IEnvironmentSettingsProvider>();
            _mockUrlValidator = new Mock<UrlValidator>(MockBehavior.Loose, "path");
            _mockEnvironmentSettings = new Mock<EnvironmentSettings>();
            _mockODataToSqlConverter = new Mock<IODataToSqlConverter>();
            _mockConnectionPoolService = new Mock<SqlConnectionPoolService>();

            // Setup environment settings
            _mockEnvironmentSettings.Setup(e => e.IsEnvironmentAllowed(It.IsAny<string>())).Returns(true);
            _mockEnvironmentSettings.Setup(e => e.AllowedEnvironments).Returns(new List<string> { "600", "700" });

            // Setup environment settings provider
            _mockEnvironmentSettingsProvider.Setup(p => p.LoadEnvironmentOrThrowAsync(It.IsAny<string>()))
                .ReturnsAsync(("Server=localhost;Database=test;Trusted_Connection=True", "localhost"));

            // Setup URL Validator
            _mockUrlValidator.Setup(v => v.IsUrlSafe(It.IsAny<string>())).Returns(true);

            // Configure the test server
            _factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.ConfigureTestServices(services =>
                    {
                        // Replace services with mocks
                        services.AddSingleton(_mockEnvironmentSettingsProvider.Object);
                        services.AddSingleton(_mockEnvironmentSettings.Object);
                        services.AddSingleton(_mockUrlValidator.Object);
                        services.AddSingleton(_mockODataToSqlConverter.Object);
                        services.AddSingleton(_mockConnectionPoolService.Object);
                        
                        // Disable rate limiting for tests
                        services.Configure<PortwayApi.Middleware.RateLimitSettings>(options =>
                        {
                            options.Enabled = false;
                        });
                        
                        // Configure minimal logging for tests
                        services.AddLogging(logging =>
                        {
                            logging.ClearProviders();
                            logging.AddConsole();
                            logging.SetMinimumLevel(LogLevel.Warning);
                        });
                    });
                });

            _client = _factory.CreateClient();
            
            // Add default authorization header with test token
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        }

        public void Dispose()
        {
            _client.Dispose();
            _factory.Dispose();
        }

        // Helper method to add authorization header
        protected void AddAuthorizationHeader(string token = "test-token")
        {
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}