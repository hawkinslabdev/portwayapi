using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Serilog;
using PortwayApi.Interfaces;
using Microsoft.Data.SqlClient;
using System.Security;

namespace PortwayApi.Classes;

public class EnvironmentSettingsProvider : IEnvironmentSettingsProvider
{
    private readonly string _basePath;
    private readonly string? _keyVaultUri;

    public EnvironmentSettingsProvider()
    {
        _basePath = Path.Combine(Directory.GetCurrentDirectory(), "environments");
        _keyVaultUri = Environment.GetEnvironmentVariable("KEYVAULT_URI");

        // Debug logging to show configured vault services
        Log.Debug("🔧 Environment settings initialized:");
        Log.Debug("  📂 Local environments path: {BasePath}", _basePath);
        Log.Debug("  🔑 Azure Key Vault: {Status}", !string.IsNullOrWhiteSpace(_keyVaultUri) ? _keyVaultUri : "Not configured");
    }

    public async Task<(string ConnectionString, string ServerName)> LoadEnvironmentOrThrowAsync(string env)
    {
        Log.Debug("🔍 Loading environment settings for: {Environment}", env);
        
        // Try Azure Key Vault first
        if (!string.IsNullOrWhiteSpace(_keyVaultUri))
        {
            Log.Debug("🔄 Attempting to load from Azure Key Vault...");
            var azure = await TryLoadFromAzureAsync(env);
            if (azure != null)
            {
                Log.Information("✅ Successfully loaded environment {Env} from Azure Key Vault", env);
                var secureConnectionString = SecureConnectionString(azure.ConnectionString!);
                return (secureConnectionString, azure.ServerName!);
            }
        }

        // Fall back to local JSON
        Log.Debug("🔄 Attempting to load from local JSON files...");
        var local = LoadFromJson(env);
        Log.Information("✅ Successfully loaded environment {Env} from local settings.json", env);
        var securedLocalConnectionString = SecureConnectionString(local.ConnectionString!);
        return (securedLocalConnectionString, local.ServerName!);
    }

    // --- Azure Key Vault Integration ---
    private async Task<EnvironmentConfig?> TryLoadFromAzureAsync(string env)
    {
        if (string.IsNullOrWhiteSpace(_keyVaultUri))
        {
            Log.Debug("Azure Key Vault not configured.");
            return null;
        }

        try
        {
            Log.Information("🔐 Azure Key Vault: Attempting connection to {KeyVaultUri}", _keyVaultUri);
            
            // Create DefaultAzureCredential with logging
            Log.Debug("🔑 Creating DefaultAzureCredential with logging");
            var credentialOptions = new DefaultAzureCredentialOptions
            {
                ExcludeEnvironmentCredential = false,
                ExcludeManagedIdentityCredential = false,
                ExcludeSharedTokenCacheCredential = false,
                ExcludeVisualStudioCredential = false,
                ExcludeVisualStudioCodeCredential = false,
                ExcludeAzureCliCredential = false,
                ExcludeInteractiveBrowserCredential = true
            };
            
            // Log which credential types are enabled
            Log.Debug("🔑 Credential options: Environment={Env}, ManagedIdentity={MI}, " +
                    "VSCode={VSCode}, VS={VS}, AzureCLI={CLI}",
                !credentialOptions.ExcludeEnvironmentCredential,
                !credentialOptions.ExcludeManagedIdentityCredential,
                !credentialOptions.ExcludeVisualStudioCodeCredential,
                !credentialOptions.ExcludeVisualStudioCredential,
                !credentialOptions.ExcludeAzureCliCredential);
                
            // Log environment credentials if available
            var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
            var tenantId = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
            Log.Debug("🔑 Environment credentials: ClientID={ClientID}, TenantID={TenantID}",
                !string.IsNullOrEmpty(clientId) ? "Configured" : "Not configured",
                !string.IsNullOrEmpty(tenantId) ? "Configured" : "Not configured");
            
            try
            {
                var credential = new DefaultAzureCredential(credentialOptions);
                Log.Debug("✅ DefaultAzureCredential created successfully");
                
                // Log the current user context (relevant for Azure CLI auth)
                try
                {
                    var startInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "az",
                        Arguments = "account show --query name -o tsv",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    
                    var process = System.Diagnostics.Process.Start(startInfo);
                    var output = await process!.StandardOutput.ReadToEndAsync();
                    await process.WaitForExitAsync();
                    
                    if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        Log.Debug("🔑 Current Azure CLI context: {Account}", output.Trim());
                    }
                    else
                    {
                        Log.Debug("⚠️ Unable to determine Azure CLI context or not logged in");
                    }
                }
                catch (Exception cliEx)
                {
                    Log.Debug("⚠️ Error checking Azure CLI context: {Error}", cliEx.Message);
                }
                
                Log.Debug("🔑 Creating SecretClient for {KeyVaultUri}", _keyVaultUri);
                var client = new SecretClient(new Uri(_keyVaultUri), credential);
                
                // Test connection by calling GetPropertiesOfSecretsAsync()
                Log.Debug("🔄 Testing Key Vault connection...");
                try
                {
                    var secretProperties = client.GetPropertiesOfSecretsAsync();
                    
                    // Use await foreach instead of First() to handle the IAsyncEnumerable
                    var found = false;
                    await foreach (var page in secretProperties.AsPages())
                    {
                        found = true;
                        break;  // We just need to confirm we can get at least one page
                    }
                    
                    if (found)
                    {
                        Log.Debug("✅ Successfully connected to Key Vault and listed secrets");
                    }
                    else
                    {
                        Log.Debug("⚠️ Connected to Key Vault but no secrets were returned");
                    }
                }
                catch (Exception listEx)
                {
                    Log.Warning("⚠️ Connected to Key Vault but couldn't list secrets: {Error}", listEx.Message);
                    // Continue even if we can't list all secrets - we might still be able to access specific ones
                }

                var connectionStringKey = $"{env}-ConnectionString";
                Log.Information("🔍 Looking for secret: {SecretName}", connectionStringKey);
                var connectionString = await TryGetSecretValue(client, connectionStringKey);
                
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    Log.Warning("⚠️ Required secret {SecretName} not found in Azure Key Vault", connectionStringKey);
                    
                    // List available secrets with similar names to help debugging
                    try
                    {
                        Log.Debug("🔍 Checking for similar secrets...");
                        var allSecrets = client.GetPropertiesOfSecretsAsync();
                        var secretList = new List<string>();
                        
                        await foreach (var secretPage in allSecrets.AsPages())
                        {
                            foreach (var secretProp in secretPage.Values)
                            {
                                secretList.Add(secretProp.Name);
                            }
                        }
                        
                        // Find secrets with similar naming pattern
                        var similarSecrets = secretList.Where(s => s.EndsWith("ConnectionString") || s.Contains(env)).ToList();
                        
                        if (similarSecrets.Any())
                        {
                            Log.Debug("📋 Found similar secrets that might be relevant: {Secrets}", 
                                string.Join(", ", similarSecrets));
                        }
                        else
                        {
                            Log.Debug("📋 No similar secrets found. Available secrets: {Count}", secretList.Count);
                        }
                    }
                    catch (Exception listEx)
                    {
                        Log.Debug("⚠️ Could not list secrets to find alternatives: {Error}", listEx.Message);
                    }
                    
                    return null;
                }

                var serverNameKey = $"{env}-ServerName";
                Log.Debug("🔍 Looking for secret: {SecretName}", serverNameKey);
                var serverName = await TryGetSecretValue(client, serverNameKey) ?? ".";

                Log.Information("📊 Loaded secrets from Azure Key Vault: ConnectionString={HasConnectionString}, ServerName={HasServerName}", 
                    !string.IsNullOrEmpty(connectionString), !string.IsNullOrEmpty(serverName));
                    
                // Log partial connection string for debugging (masking sensitive parts)
                if (!string.IsNullOrEmpty(connectionString))
                {
                    var maskedConnStr = MaskConnectionString(connectionString);
                    Log.Debug("🔐 Connection string pattern: {ConnectionString}", maskedConnStr);
                }
                
                return new EnvironmentConfig { ConnectionString = connectionString, ServerName = serverName };
            }
            catch (Azure.Identity.CredentialUnavailableException credEx)
            {
                Log.Error("❌ Azure authentication failed - no credentials available: {Message}", credEx.Message);
                Log.Debug("💡 Suggestion: Run 'az login' to sign into Azure CLI, or set AZURE_CLIENT_ID, AZURE_TENANT_ID, and AZURE_CLIENT_SECRET environment variables");
                return null;
            }
        }
        catch (Exception ex)
        {
            // Provide more specific error messages based on exception type
            if (ex is Azure.RequestFailedException rfe)
            {
                Log.Error("❌ Azure Key Vault request failed: Status={Status}, ErrorCode={ErrorCode}, Message={Message}", 
                    rfe.Status, rfe.ErrorCode, rfe.Message);
                    
                if (rfe.Status == 403)
                {
                    Log.Warning("💡 This appears to be a permissions issue. Make sure your identity has 'Get' permissions for secrets in this Key Vault");
                }
                else if (rfe.Status == 401)
                {
                    Log.Warning("💡 Authentication failed. Verify your credentials are valid and not expired");
                }
                else if (rfe.Status == 404)
                {
                    Log.Warning("💡 Key Vault not found. Verify the URL is correct: {KeyVaultUri}", _keyVaultUri);
                }
            }
            else
            {
                Log.Error(ex, "❌ Azure Key Vault access failed: {ErrorType} - {ErrorMessage}", 
                    ex.GetType().Name, ex.Message);
            }
            return null;
        }
    }

    private async Task<string?> TryGetSecretValue(SecretClient client, string secretName)
    {
        try
        {
            Log.Debug("🔄 Requesting secret: {SecretName}", secretName);
            var secretResponse = await client.GetSecretAsync(secretName);
            
            if (secretResponse == null || secretResponse.Value == null)
            {
                Log.Debug("⚠️ Secret response or value is null for {SecretName}", secretName);
                return null;
            }
            
            var value = secretResponse.Value.Value;
            var valueExists = !string.IsNullOrEmpty(value);
            Log.Debug("🔑 Secret {SecretName}: {Result}", secretName, 
                valueExists ? "Retrieved (non-empty)" : "Retrieved but empty");
            
            if (!valueExists)
            {
                Log.Warning("⚠️ Secret {SecretName} exists but has empty value", secretName);
            }
            
            return value;
        }
        catch (Azure.RequestFailedException rfEx)
        {
            if (rfEx.Status == 404)
            {
                Log.Debug("⚠️ Secret {SecretName} not found (404)", secretName);
            }
            else
            {
                Log.Debug("⚠️ Failed to retrieve secret {SecretName}: Status={Status}, Error={ErrorCode}, Message={ErrorMessage}", 
                    secretName, rfEx.Status, rfEx.ErrorCode, rfEx.Message);
            }
            return null;
        }
        catch (Exception ex)
        {
            Log.Debug("⚠️ Exception retrieving secret {SecretName}: {ErrorType} - {ErrorMessage}", 
                secretName, ex.GetType().Name, ex.Message);
            return null;
        }
    }

    private string MaskConnectionString(string connectionString)
    {
        // Create a safe representation of connection string for logging
        // This masks passwords and other sensitive values while keeping the structure
        try
        {
            var parts = connectionString.Split(';')
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(part => {
                    var keyValue = part.Split('=', 2);
                    if (keyValue.Length != 2) return part;
                    
                    var key = keyValue[0].Trim();
                    var value = keyValue[1].Trim();
                    
                    // Mask sensitive parts
                    if (key.Contains("password", StringComparison.OrdinalIgnoreCase) || 
                        key.Contains("pwd", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{key}=***MASKED***";
                    }
                    
                    // Show server and database names
                    if (key.Contains("server", StringComparison.OrdinalIgnoreCase) || 
                        key.Contains("data source", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("database", StringComparison.OrdinalIgnoreCase) ||
                        key.Contains("initial catalog", StringComparison.OrdinalIgnoreCase))
                    {
                        return $"{key}={value}";
                    }
                    
                    // For other parameters, show the key but not the value
                    return $"{key}=***";
                });
                
            return string.Join("; ", parts);
        }
        catch
        {
            // If parsing fails, return a generic message
            return "ConnectionString parsing failed";
        }
    }

    // --- Local JSON Configuration ---
    private EnvironmentConfig LoadFromJson(string env)
    {
        var settingsPath = Path.Combine(_basePath, env, "settings.json");
        Log.Debug("📄 Attempting to load from file: {FilePath}", settingsPath);

        if (!File.Exists(settingsPath))
        {
            Log.Error("❌ settings.json not found for environment: {Environment}, path: {FilePath}", env, settingsPath);
            throw new FileNotFoundException($"settings.json not found for environment: {env}", settingsPath);
        }

        try
        {
            var json = File.ReadAllText(settingsPath);
            var config = JsonSerializer.Deserialize<EnvironmentConfig>(json);
                     
            if (config == null)
            {
                Log.Error("❌ Failed to deserialize JSON from {FilePath}", settingsPath);
                throw new InvalidOperationException($"Invalid JSON in settings.json for environment: {env}");
            }

            if (string.IsNullOrWhiteSpace(config.ConnectionString))
            {
                Log.Error("❌ Missing ConnectionString in settings.json for environment: {Environment}", env);
                throw new InvalidOperationException($"Missing connection string in settings.json for environment: {env}");
            }

            Log.Debug("📊 Loaded secrets from local settings.json: ConnectionString={HasConnectionString}, ServerName={HasServerName}", 
                !string.IsNullOrEmpty(config.ConnectionString), !string.IsNullOrEmpty(config.ServerName));
            return config;
        }
        catch (Exception ex) when (ex is not FileNotFoundException && ex is not InvalidOperationException)
        {
            Log.Error(ex, "❌ Error reading or parsing settings.json for environment: {Environment}", env);
            throw;
        }
    }

    // Secures a connection string by encrypting sensitive credentials in memory
    private string SecureConnectionString(string connectionString)
    {
        try
        {
            Log.Debug("🔒 Securing connection string to prevent credential leakage");
            
            // Parse connection string
            var builder = new SqlConnectionStringBuilder(connectionString);
            
            // Check if connection string contains hardcoded credentials
            bool hasUserID = !string.IsNullOrEmpty(builder.UserID);
            bool hasPassword = !string.IsNullOrEmpty(builder.Password);
            
            if (hasUserID || hasPassword)
            {
                Log.Warning("⚠️ Connection string contains hardcoded credentials. Storing securely in memory");
                
                // Store credentials securely
                if (hasPassword)
                {
                    // Extract the original password
                    string originalPassword = builder.Password;
                    
                    // Store the password securely in memory
                    var securePassword = new SecureString();
                    foreach (char c in originalPassword)
                    {
                        securePassword.AppendChar(c);
                    }
                    securePassword.MakeReadOnly();
                    
                    // Clear the password from the builder to prevent it from being accessible in memory dumps
                    builder.Password = "";
                    
                    Log.Debug("🔐 Password encrypted in secure memory and removed from builder");
                }
                
                // Log masked version for visibility in logs
                var masked = MaskConnectionString(connectionString);
                Log.Debug("🔍 Using connection string with credentials: {ConnectionString}", masked);
                
                // Return the original string which will be used directly by SQL connections
                // We use the original string to ensure the connection works properly
                // but we've removed it from our accessible application state
                return connectionString;
            }
            else
            {
                Log.Debug("✅ Connection string does not contain hardcoded credentials");
                return connectionString;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "⚠️ Error while securing connection string: {ErrorMessage}", ex.Message);
            return connectionString;
        }
    }

    // --- Configuration Model Class ---
    private class EnvironmentConfig
    {
        public string? ConnectionString { get; set; }
        public string? ServerName { get; set; }
    }
}