namespace PortwayApi.Auth;

using Microsoft.EntityFrameworkCore;
using Serilog;

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }
    public DbSet<AuthToken> Tokens { get; set; }

    public void EnsureTablesCreated()
    {
        try
        {
            // First check if the table exists
            bool tableExists = false;
            try
            {
                // Use ExecuteSqlRaw with proper result handling
                using var cmd = Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Tokens'";
                
                if (Database.GetDbConnection().State != System.Data.ConnectionState.Open)
                    Database.GetDbConnection().Open();
                    
                var result = cmd.ExecuteScalar();
                tableExists = Convert.ToInt32(result) > 0;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error checking if Tokens table exists");
                return;
            }
            
            if (tableExists)
            {
                // Table exists, check if it has the required column
                bool hasRequiredColumn = false;
                try
                {
                    using var cmd = Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Tokens') WHERE name='TokenSalt'";
                    var result = cmd.ExecuteScalar();
                    hasRequiredColumn = Convert.ToInt32(result) > 0;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error checking Tokens table schema");
                    return;
                }
                
                if (hasRequiredColumn)
                {
                    // Table exists with correct schema
                    Log.Debug("Tokens table exists with correct schema");
                    return;
                }
                
                // Table exists but with wrong schema, drop it
                Log.Information("Tokens table exists but with wrong schema, recreating...");
                try
                {
                    Database.ExecuteSqlRaw("DROP TABLE IF EXISTS Tokens");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error dropping Tokens table");
                    return;
                }
            }
            
            // Create the table with the correct schema
            try
            {
                Database.ExecuteSqlRaw(@"
                    CREATE TABLE Tokens (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT, 
                        Username TEXT NOT NULL DEFAULT 'legacy',
                        TokenHash TEXT NOT NULL DEFAULT '', 
                        TokenSalt TEXT NOT NULL DEFAULT '',
                        CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP,
                        RevokedAt DATETIME NULL
                    )");
                
                Log.Information("Created new Tokens table with correct schema");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error creating Tokens table: {Message}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error ensuring Tokens table is created");
        }
    }
}