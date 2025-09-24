using Microsoft.EntityFrameworkCore;
using LancacheManager.Models;

namespace LancacheManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    public DbSet<Download> Downloads { get; set; }
    public DbSet<ClientStats> ClientStats { get; set; }
    public DbSet<ServiceStats> ServiceStats { get; set; }
    public DbSet<SteamDepotMapping> SteamDepotMappings { get; set; }
    
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

        // Unique constraint on the combination of DepotId and AppId
        modelBuilder.Entity<SteamDepotMapping>()
            .HasIndex(m => new { m.DepotId, m.AppId })
            .HasDatabaseName("IX_SteamDepotMappings_DepotId_AppId")
            .IsUnique();
    }
}