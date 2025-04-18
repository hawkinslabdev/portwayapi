using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace TokenGenerator
{
    public class AuthToken
    {
        public int Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string TokenHash { get; set; } = string.Empty;
        public string TokenSalt { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    public class AuthDbContext : DbContext
    {
        public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }

        public DbSet<AuthToken> Tokens { get; set; }

        
        public bool IsValidDatabase()
        {
            try
            {
                
                return Tokens.Any();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Database validation failed");
                return false;
            }
        }
    }

    public class CommandLineOptions
    {
        public string? DatabasePath { get; set; }
        public string? TokensFolder { get; set; }
        public string? Username { get; set; }
        public bool ShowHelp { get; set; }
    }

    public class AppConfig
    {
        public string DatabasePath { get; set; } = "auth.db";
        public string TokensFolder { get; set; } = "tokens";
    }

    public class TokenService
    {
        private readonly AuthDbContext _dbContext;
        private readonly string _tokenFolderPath;

        public TokenService(AuthDbContext dbContext, AppConfig config)
        {
            _dbContext = dbContext;
            
            
            _tokenFolderPath = !Path.IsPathRooted(config.TokensFolder) 
                ? Path.GetFullPath(config.TokensFolder) 
                : config.TokensFolder;
            
            if (!Directory.Exists(_tokenFolderPath))
            {
                Directory.CreateDirectory(_tokenFolderPath);
                Log.Information("Created tokens directory at {Path}", _tokenFolderPath);
            }
        }
        
        public async Task<string> GenerateTokenAsync(string username)
        {
			// Generate a random token
			string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
				.Replace("+", "-") // Replace '+' with URL-safe '-'
				.Replace("/", "_") // Replace '/' with URL-safe '_'
				.TrimEnd('=');     // Remove padding '=' for URL safety       
            
            byte[] salt = GenerateSalt();
            string saltString = Convert.ToBase64String(salt);
            
            string hashedToken = HashToken(token, salt);
            
            var tokenEntry = new AuthToken
            {
                Username = username,
                TokenHash = hashedToken,
                TokenSalt = saltString,
                CreatedAt = DateTime.UtcNow
            };
            
            _dbContext.Tokens.Add(tokenEntry);
            await _dbContext.SaveChangesAsync();
            
            await SaveTokenToFileAsync(username, token);
            
            return token;
        }

        public async Task<List<AuthToken>> GetAllTokensAsync()
        {
            return await _dbContext.Tokens.ToListAsync();
        }

        public async Task<bool> RevokeTokenAsync(int id)
        {
            var token = await _dbContext.Tokens.FindAsync(id);
            if (token == null)
            {
                return false;
            }

            
            string filePath = Path.Combine(_tokenFolderPath, $"{token.Username}.txt");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                Log.Information("Deleted token file for {Username}", token.Username);
            }

            
            _dbContext.Tokens.Remove(token);
            await _dbContext.SaveChangesAsync();
            return true;
        }
        
        private string HashToken(string token, byte[] salt)
        {
            using (var pbkdf2 = new Rfc2898DeriveBytes(token, salt, 10000, HashAlgorithmName.SHA256))
            {
                byte[] hash = pbkdf2.GetBytes(32);
                return Convert.ToBase64String(hash);
            }
        }
        
        private byte[] GenerateSalt()
        {
            byte[] salt = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }
        
        private async Task SaveTokenToFileAsync(string username, string token)
        {
            try
            {
                
                if (!Directory.Exists(_tokenFolderPath))
                {
                    Directory.CreateDirectory(_tokenFolderPath);
                    Log.Information("Created tokens directory at {Path}", _tokenFolderPath);
                }
                
                string filePath = Path.Combine(_tokenFolderPath, $"{username}.txt");
                await File.WriteAllTextAsync(filePath, token);
                Log.Information("Token file saved to {FilePath}", filePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save token file for user {Username} at {Path}", username, _tokenFolderPath);
                throw;
            }
        }

        // Make the token folder path accessible
        public string GetTokenFolderPath()
        {
            return _tokenFolderPath;
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            
            var options = ParseCommandLineArguments(args);
            
            
            if (options.ShowHelp)
            {
                DisplayHelp();
                return;
            }

            
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .MinimumLevel.Information()
                .CreateLogger();

            Log.Information("Starting Token Generator...");

            try
            {
                var (serviceProvider, config) = ConfigureServices(options);
                
                using (var scope = serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
                    
                    Log.Information("Using database at: {DbPath}", config.DatabasePath);
                    
                    if (!File.Exists(config.DatabasePath))
                    {
                        DisplayErrorAndExit($"Database not found at {config.DatabasePath}. Please run the main application first or configure the correct path using -d or --database parameter or in appsettings.json.");
                        return;
                    }
                    
                    if (!dbContext.IsValidDatabase())
                    {
                        DisplayErrorAndExit($"Invalid database structure at {config.DatabasePath}. Please run the main application first.");
                        return;
                    }
                }

                
                if (!string.IsNullOrWhiteSpace(options.Username))
                {
                    await GenerateTokenForUserAsync(options.Username, serviceProvider);
                    return;
                }

                bool exitRequested = false;

                while (!exitRequested)
                {
                    DisplayMenu();
                    string choice = Console.ReadLine() ?? "";

                    switch (choice)
                    {
                        case "1":
                            await ListAllTokensAsync(serviceProvider);
                            break;
                        case "2":
                            await AddNewTokenAsync(serviceProvider);
                            break;
                        case "3":
                            await RevokeTokenAsync(serviceProvider);
                            break;
                        case "0":
                            exitRequested = true;
                            break;
                        default:
                            Console.WriteLine("Invalid option. Please try again.");
                            break;
                    }

                    if (!exitRequested)
                    {
                        Console.WriteLine("\nPress any key to return to menu...");
                        Console.ReadKey();
                        Console.Clear();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "An error occurred: {ErrorMessage}", ex.Message);
                DisplayErrorAndExit($"An error occurred: {ex.Message}");
            }
            finally
            {
                Log.CloseAndFlush();
            }
        }

        static void DisplayHelp()
        {
            Console.WriteLine("PortwayApi Token Generator");
            Console.WriteLine("===========================");
            Console.WriteLine("A utility to manage authentication tokens for the PortwayApi service.");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  TokenGenerator.exe [options] [username]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -h, --help                 Show this help message");
            Console.WriteLine("  -d, --database <path>      Specify the path to the auth.db file");
            Console.WriteLine("  -t, --tokens <path>        Specify the folder to store token files");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  TokenGenerator.exe                           Run in interactive mode");
            Console.WriteLine("  TokenGenerator.exe -d \"C:\\path\\to\\auth.db\"   Use specific database file");
            Console.WriteLine("  TokenGenerator.exe admin                     Generate token for user 'admin'");
            Console.WriteLine("  TokenGenerator.exe -d \"..\\auth.db\" admin      Generate token with custom DB path");
        }

        static void DisplayErrorAndExit(string errorMessage)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\nERROR: " + errorMessage);
            Console.ResetColor();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            Environment.Exit(1);
        }
        
        static CommandLineOptions ParseCommandLineArguments(string[] args)
        {
            var options = new CommandLineOptions();
            
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i].ToLowerInvariant();
                
                switch (arg)
                {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        return options;
                        
                    case "-d":
                    case "--database":
                        if (i + 1 < args.Length)
                        {
                            options.DatabasePath = args[++i];
                        }
                        break;
                        
                    case "-t":
                    case "--tokens":
                        if (i + 1 < args.Length)
                        {
                            options.TokensFolder = args[++i];
                        }
                        break;
                        
                    default:
                        
                        if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(options.Username))
                        {
                            options.Username = args[i];
                        }
                        break;
                }
            }
            
            return options;
        }

        static void DisplayMenu()
        {
            Console.WriteLine("===============================================");
            Console.WriteLine("      PortwayApi Token Generator        ");
            Console.WriteLine("===============================================");
            Console.WriteLine("1. List all existing tokens");
            Console.WriteLine("2. Generate new token");
            Console.WriteLine("3. Revoke token");
            Console.WriteLine("0. Exit");
            Console.WriteLine("-----------------------------------------------");
            Console.Write("Select an option: ");
        }

        static async Task ListAllTokensAsync(IServiceProvider serviceProvider)
        {
            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            var tokens = await tokenService.GetAllTokensAsync();

            if (tokens.Count == 0)
            {
                Console.WriteLine("\nNo tokens found in the database.");
                return;
            }

            Console.WriteLine("\n=== Existing Tokens ===");
            Console.WriteLine($"{"ID",-5} {"Username",-20} {"Created",-20} {"Token File",-15}");
            Console.WriteLine(new string('-', 60));

            var config = scope.ServiceProvider.GetRequiredService<AppConfig>();
            string tokenFolderPath = tokenService.GetTokenFolderPath();
            
            foreach (var token in tokens)
            {                
                string tokenFilePath = Path.Combine(tokenFolderPath, $"{token.Username}.txt");
                string tokenFileStatus = File.Exists(tokenFilePath) ? "Available" : "Missing";

                Console.WriteLine($"{token.Id,-5} {token.Username,-20} {token.CreatedAt.ToString("yyyy-MM-dd HH:mm"),-20} {tokenFileStatus,-15}");
            }
        }

        static async Task AddNewTokenAsync(IServiceProvider serviceProvider)
        {
            Console.WriteLine("\n=== Generate New Token ===");
            Console.Write("Enter username (leave blank for machine name): ");
            string? input = Console.ReadLine();
            string username = string.IsNullOrWhiteSpace(input) ? Environment.MachineName : input;

            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
            var config = scope.ServiceProvider.GetRequiredService<AppConfig>();

            try
            {
                Console.WriteLine($"Generating token for user: {username}");
                var token = await tokenService.GenerateTokenAsync(username);

                Console.WriteLine("\n--- Token Generated Successfully ---");
                Console.WriteLine($"Username: {username}");
                Console.WriteLine($"Token: {token}");
                
                string tokenFolderPath = tokenService.GetTokenFolderPath();
                
                Console.WriteLine($"Token file: {Path.Combine(tokenFolderPath, $"{username}.txt")}");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating token: {ErrorMessage}", ex.Message);
                Console.WriteLine($"\nError generating token: {ex.Message}");
            }
        }

        static async Task RevokeTokenAsync(IServiceProvider serviceProvider)
        {
            
            await ListAllTokensAsync(serviceProvider);

            Console.WriteLine("\n=== Revoke Token ===");
            Console.Write("Enter token ID to revoke (or 0 to cancel): ");
            
            if (!int.TryParse(Console.ReadLine(), out int tokenId) || tokenId <= 0)
            {
                Console.WriteLine("Operation cancelled.");
                return;
            }

            using var scope = serviceProvider.CreateScope();
            var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

            bool result = await tokenService.RevokeTokenAsync(tokenId);
            if (result)
            {
                Console.WriteLine($"Token with ID {tokenId} has been revoked successfully.");
            }
            else
            {
                Console.WriteLine($"Token with ID {tokenId} not found.");
            }
        }

        static async Task GenerateTokenForUserAsync(string username, IServiceProvider serviceProvider)
        {
            Log.Information("Token Generator - Automated Mode");
            Log.Information("=================================");

            try
            {                
                using var scope = serviceProvider.CreateScope();
                var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();
                var config = scope.ServiceProvider.GetRequiredService<AppConfig>();

                Log.Information("Generating token for user: {Username}", username);
                var token = await tokenService.GenerateTokenAsync(username);
                
                Log.Information("Token generation successful!");
                Log.Information("Username: {Username}", username);
                Log.Information("Token: {Token}", token);
                Log.Information("Token file: {FilePath}", Path.GetFullPath(Path.Combine(config.TokensFolder, $"{username}.txt")));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error generating token: {ErrorMessage}", ex.Message);
                DisplayErrorAndExit($"Error generating token: {ex.Message}");
            }
        }

        static (IServiceProvider ServiceProvider, AppConfig Config) ConfigureServices(CommandLineOptions cliOptions)
        {
            var services = new ServiceCollection();
            var config = LoadConfiguration();
            
            
            if (!string.IsNullOrWhiteSpace(cliOptions.DatabasePath))
            {
                config.DatabasePath = cliOptions.DatabasePath;
                Log.Information("Using database path from command line: {Path}", config.DatabasePath);
            }
            
            if (!string.IsNullOrWhiteSpace(cliOptions.TokensFolder))
            {
                config.TokensFolder = cliOptions.TokensFolder;
                Log.Information("Using tokens folder from command line: {Path}", config.TokensFolder);
            }
            
            
            services.AddSingleton(config);
            
            
            if (!Path.IsPathRooted(config.DatabasePath))
            {
                config.DatabasePath = Path.GetFullPath(config.DatabasePath);
            }
            
            if (!Path.IsPathRooted(config.TokensFolder))
            {
                config.TokensFolder = Path.GetFullPath(config.TokensFolder);
            }
            
            
            if (!Directory.Exists(config.TokensFolder))
            {
                Directory.CreateDirectory(config.TokensFolder);
                Log.Information("Created tokens directory at {Path}", config.TokensFolder);
            }
            
            Log.Information("Database path: {DbPath}", config.DatabasePath);
            Log.Information("Tokens folder: {TokensFolder}", config.TokensFolder);
            
            services.AddDbContext<AuthDbContext>(options =>
                options.UseSqlite($"Data Source={config.DatabasePath}"));

            services.AddScoped<TokenService>();
            
            return (services.BuildServiceProvider(), config);
        }
        
        static AppConfig LoadConfiguration()
        {
            var config = new AppConfig();
            string configFileName = "appsettings.json";
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFileName);
            
            
            if (!File.Exists(configFilePath))
            {
                Log.Information("Configuration file not found. Creating default configuration.");
                CreateDefaultConfig(configFilePath);
            }
            
            try
            {
                var configJson = File.ReadAllText(configFilePath);
                var configValues = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson);
                
                if (configValues != null)
                {
                    if (configValues.TryGetValue("DatabasePath", out var dbPathElement) && 
                        dbPathElement.ValueKind == JsonValueKind.String)
                    {
                        config.DatabasePath = dbPathElement.GetString() ?? "auth.db";
                    }
                    
                    if (configValues.TryGetValue("TokensFolder", out var tokensFolderElement) && 
                        tokensFolderElement.ValueKind == JsonValueKind.String)
                    {
                        config.TokensFolder = tokensFolderElement.GetString() ?? "tokens";
                    }
                }
                
                Log.Information("Configuration loaded successfully.");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error loading configuration. Using default values.");
            }
            
            
            if (!Path.IsPathRooted(config.TokensFolder))
            {
                config.TokensFolder = Path.GetFullPath(config.TokensFolder);
            }
            
            return config;
        }
        
        static void CreateDefaultConfig(string configFilePath)
        {
            
            var directory = Path.GetDirectoryName(configFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            var defaultConfig = new AppConfig
            {
                DatabasePath = "../../auth.db",  
                TokensFolder = "tokens"       
            };
            
            try
            {
                var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(defaultConfig, jsonOptions);
                File.WriteAllText(configFilePath, json);
                Log.Information("Created default configuration file at {Path}", configFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to create default configuration file");
            }
        }
    }
}