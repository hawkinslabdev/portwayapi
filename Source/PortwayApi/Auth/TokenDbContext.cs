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
                // Table exists, no need to create it
                Log.Debug("✅ Tokens table already exists");
                return;
            }
            
            // Create the table with the current schema
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
                
                Log.Debug("✅ Created new Tokens table");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error creating Tokens table: {Message}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error ensuring Tokens table is created");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        
        // Configure the Tokens table
        modelBuilder.Entity<AuthToken>(entity =>
        {
            entity.ToTable("Tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Username).IsRequired();
            entity.Property(e => e.TokenHash).IsRequired();
            entity.Property(e => e.TokenSalt).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.Property(e => e.RevokedAt).IsRequired(false);
        });
    }
}