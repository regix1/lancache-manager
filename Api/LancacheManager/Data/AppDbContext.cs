using Microsoft.EntityFrameworkCore;
using LancacheManager.Models;

namespace LancacheManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
        // Optimize SQLite for bulk inserts
        if (Database.IsSqlite())
        {
            Database.ExecuteSqlRaw("PRAGMA journal_mode = WAL;");          // Write-Ahead Logging for better concurrency
            Database.ExecuteSqlRaw("PRAGMA synchronous = NORMAL;");        // Faster but still safe
            Database.ExecuteSqlRaw("PRAGMA cache_size = 1000000;");        // 1GB cache (1000000 pages)
            Database.ExecuteSqlRaw("PRAGMA locking_mode = EXCLUSIVE;");    // Faster for single-writer scenarios
            Database.ExecuteSqlRaw("PRAGMA temp_store = MEMORY;");         // Keep temp tables in memory
        }
    }

    public DbSet<Download> Downloads { get; set; }
    public DbSet<ClientStats> ClientStats { get; set; }
    public DbSet<ServiceStats> ServiceStats { get; set; }
    public DbSet<SteamDepotMapping> SteamDepotMappings { get; set; }
    public DbSet<LogEntryRecord> LogEntries { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Primary keys
        modelBuilder.Entity<ClientStats>().HasKey(c => c.ClientIp);
        modelBuilder.Entity<ServiceStats>().HasKey(s => s.Service);
        
        // Downloads indexes for fast queries
        modelBuilder.Entity<Download>()
            .HasIndex(d => new { d.ClientIp, d.Service, d.IsActive })
            .HasDatabaseName("IX_Downloads_Client_Service_Active");
            
        modelBuilder.Entity<Download>()
            .HasIndex(d => d.StartTime)
            .HasDatabaseName("IX_Downloads_StartTime")
            .IsDescending(); // For ORDER BY DESC queries
            
        modelBuilder.Entity<Download>()
            .HasIndex(d => d.IsActive)
            .HasDatabaseName("IX_Downloads_IsActive");
            
        modelBuilder.Entity<Download>()
            .HasIndex(d => d.EndTime)
            .HasDatabaseName("IX_Downloads_EndTime");
            
        // ClientStats indexes
        modelBuilder.Entity<ClientStats>()
            .HasIndex(c => c.LastSeen)
            .HasDatabaseName("IX_ClientStats_LastSeen");
            
        // ServiceStats indexes  
        modelBuilder.Entity<ServiceStats>()
            .HasIndex(s => s.LastActivity)
            .HasDatabaseName("IX_ServiceStats_LastActivity");
            
        // SteamDepotMapping indexes
        modelBuilder.Entity<SteamDepotMapping>()
            .HasIndex(m => m.DepotId)
            .HasDatabaseName("IX_SteamDepotMappings_DepotId");

        modelBuilder.Entity<SteamDepotMapping>()
            .HasIndex(m => m.AppId)
            .HasDatabaseName("IX_SteamDepotMappings_AppId");

        // LogEntryRecord indexes for performance
        modelBuilder.Entity<LogEntryRecord>()
            .HasIndex(l => new { l.ClientIp, l.Service })
            .HasDatabaseName("IX_LogEntries_Client_Service");

        modelBuilder.Entity<LogEntryRecord>()
            .HasIndex(l => l.Timestamp)
            .HasDatabaseName("IX_LogEntries_Timestamp");

        modelBuilder.Entity<LogEntryRecord>()
            .HasIndex(l => l.DownloadId)
            .HasDatabaseName("IX_LogEntries_DownloadId");

        // Composite index for efficient duplicate detection during reprocessing
        modelBuilder.Entity<LogEntryRecord>()
            .HasIndex(l => new { l.ClientIp, l.Service, l.Timestamp, l.Url, l.BytesServed })
            .HasDatabaseName("IX_LogEntries_DuplicateCheck");

        // Unique constraint on the combination of DepotId and AppId
        modelBuilder.Entity<SteamDepotMapping>()
            .HasIndex(m => new { m.DepotId, m.AppId })
            .HasDatabaseName("IX_SteamDepotMappings_DepotId_AppId")
            .IsUnique();
    }
}