using PortwayApi.Classes;
using System.Text.Json;
using Xunit;

namespace PortwayApi.Tests.Services
{
    public class EnvironmentSettingsTests
    {
        private readonly string _testSettingsPath;
        
        public EnvironmentSettingsTests()
        {
            // Create a temporary directory for test settings
            var tempDir = Path.Combine(Path.GetTempPath(), "PortwayApiTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);
            
            _testSettingsPath = Path.Combine(tempDir, "settings.json");
            
            // Setup cleanup on test completion
            _cleanup = () => {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            };
        }
        
        private readonly Action _cleanup;
        
        public void Dispose()
        {
            _cleanup();
        }
        
        [Fact]
        public void IsEnvironmentAllowed_ValidEnvironment_ReturnsTrue()
        {
            // Arrange
            CreateTestSettings(new[] { "test", "prod" });
            var environmentSettings = new TestableEnvironmentSettings(_testSettingsPath);
            
            // Act
            bool result = environmentSettings.IsEnvironmentAllowed("test");
            
            // Assert
            Assert.True(result);
        }
        
        [Fact]
        public void IsEnvironmentAllowed_InvalidEnvironment_ReturnsFalse()
        {
            // Arrange
            CreateTestSettings(new[] { "test", "prod" });
            var environmentSettings = new TestableEnvironmentSettings(_testSettingsPath);
            
            // Act
            bool result = environmentSettings.IsEnvironmentAllowed("dev");
            
            // Assert
            Assert.False(result);
        }
        
        [Fact]
        public void GetAllowedEnvironments_ReturnsConfiguredEnvironments()
        {
            // Arrange
            string[] expectedEnvironments = { "600", "700", "test" };
            CreateTestSettings(expectedEnvironments);
            var environmentSettings = new TestableEnvironmentSettings(_testSettingsPath);
            
            // Act
            var result = environmentSettings.GetAllowedEnvironments();
            
            // Assert
            Assert.Equal(expectedEnvironments.Length, result.Count);
            foreach (var env in expectedEnvironments)
            {
                Assert.Contains(env, result);
            }
        }
        
        [Fact]
        public void Constructor_MissingSettingsFile_CreatesDefaultSettings()
        {
            // Arrange - don't create a settings file
            var environmentSettings = new TestableEnvironmentSettings(_testSettingsPath);
            
            // Act
            var result = environmentSettings.GetAllowedEnvironments();
            
            // Assert
            Assert.Contains("600", result);
            Assert.Contains("700", result);
            Assert.True(File.Exists(_testSettingsPath));
        }
        
        private void CreateTestSettings(string[] allowedEnvironments)
        {
            var settings = new
            {
                Environment = new
                {
                    ServerName = "testserver",
                    AllowedEnvironments = allowedEnvironments
                }
            };
            
            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_testSettingsPath, json);
        }
        
        // Testable version of EnvironmentSettings that allows us to set the settings path
        private class TestableEnvironmentSettings : EnvironmentSettings
        {
            private readonly string _settingsPath;
            
            public TestableEnvironmentSettings(string settingsPath)
            {
                _settingsPath = settingsPath;
                LoadSettings();
            }
            
            protected override string GetSettingsPath()
            {
                return _settingsPath;
            }
            
            // Expose protected methods for testing
            public new void LoadSettings()
            {
                base.LoadSettings();
            }
        }
    }
}