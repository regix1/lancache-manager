using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Download> Downloads { get; set; }
    public DbSet<ClientStats> ClientStats { get; set; }
    public DbSet<ServiceStats> ServiceStats { get; set; }
    public DbSet<SteamDepotMapping> SteamDepotMappings { get; set; }
    public DbSet<LogEntryRecord> LogEntries { get; set; }
    public DbSet<CachedGameDetection> CachedGameDetections { get; set; }
    public DbSet<CachedServiceDetection> CachedServiceDetections { get; set; }
    public DbSet<UserSession> UserSessions { get; set; }
    public DbSet<UserPreferences> UserPreferences { get; set; }
    public DbSet<Event> Events { get; set; }
    public DbSet<EventDownload> EventDownloads { get; set; }
    public DbSet<ClientGroup> ClientGroups { get; set; }
    public DbSet<ClientGroupMember> ClientGroupMembers { get; set; }

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
            .HasIndex(d => d.StartTimeUtc)
            .HasDatabaseName("IX_Downloads_StartTime")
            .IsDescending(); // For ORDER BY DESC queries

        modelBuilder.Entity<Download>()
            .HasIndex(d => d.IsActive)
            .HasDatabaseName("IX_Downloads_IsActive");

        modelBuilder.Entity<Download>()
            .HasIndex(d => d.EndTimeUtc)
            .HasDatabaseName("IX_Downloads_EndTime");

        // Datasource index for multi-datasource filtering
        modelBuilder.Entity<Download>()
            .HasIndex(d => d.Datasource)
            .HasDatabaseName("IX_Downloads_Datasource");

        // ClientStats indexes
        modelBuilder.Entity<ClientStats>()
            .HasIndex(c => c.LastActivityUtc)
            .HasDatabaseName("IX_ClientStats_LastActivityUtc");

        // ServiceStats indexes
        modelBuilder.Entity<ServiceStats>()
            .HasIndex(s => s.LastActivityUtc)
            .HasDatabaseName("IX_ServiceStats_LastActivityUtc");

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

        // Datasource index for multi-datasource filtering
        modelBuilder.Entity<LogEntryRecord>()
            .HasIndex(l => l.Datasource)
            .HasDatabaseName("IX_LogEntries_Datasource");

        // Unique constraint on the combination of DepotId and AppId
        modelBuilder.Entity<SteamDepotMapping>()
            .HasIndex(m => new { m.DepotId, m.AppId })
            .HasDatabaseName("IX_SteamDepotMappings_DepotId_AppId")
            .IsUnique();

        // CachedGameDetection indexes
        modelBuilder.Entity<CachedGameDetection>()
            .HasIndex(c => c.GameAppId)
            .HasDatabaseName("IX_CachedGameDetection_GameAppId")
            .IsUnique();

        modelBuilder.Entity<CachedGameDetection>()
            .HasIndex(c => c.LastDetectedUtc)
            .HasDatabaseName("IX_CachedGameDetection_LastDetectedUtc");

        // CachedServiceDetection indexes
        modelBuilder.Entity<CachedServiceDetection>()
            .HasIndex(c => c.ServiceName)
            .HasDatabaseName("IX_CachedServiceDetection_ServiceName")
            .IsUnique();

        modelBuilder.Entity<CachedServiceDetection>()
            .HasIndex(c => c.LastDetectedUtc)
            .HasDatabaseName("IX_CachedServiceDetection_LastDetectedUtc");

        // UserSession indexes
        modelBuilder.Entity<UserSession>()
            .HasIndex(s => s.IsGuest)
            .HasDatabaseName("IX_UserSessions_IsGuest");

        modelBuilder.Entity<UserSession>()
            .HasIndex(s => s.LastSeenAtUtc)
            .HasDatabaseName("IX_UserSessions_LastSeenAtUtc");

        modelBuilder.Entity<UserSession>()
            .HasIndex(s => s.ExpiresAtUtc)
            .HasDatabaseName("IX_UserSessions_ExpiresAtUtc");

        modelBuilder.Entity<UserSession>()
            .HasIndex(s => s.IsRevoked)
            .HasDatabaseName("IX_UserSessions_IsRevoked");

        // UserPreferences configuration
        modelBuilder.Entity<UserPreferences>()
            .HasIndex(p => p.DeviceId)
            .HasDatabaseName("IX_UserPreferences_DeviceId")
            .IsUnique();

        // Configure one-to-one relationship between UserSession and UserPreferences
        modelBuilder.Entity<UserSession>()
            .HasOne(s => s.Preferences)
            .WithOne(p => p.Session)
            .HasForeignKey<UserPreferences>(p => p.DeviceId)
            .OnDelete(DeleteBehavior.Cascade);

        // Event indexes
        modelBuilder.Entity<Event>()
            .HasIndex(e => e.StartTimeUtc)
            .HasDatabaseName("IX_Events_StartTimeUtc");

        modelBuilder.Entity<Event>()
            .HasIndex(e => e.EndTimeUtc)
            .HasDatabaseName("IX_Events_EndTimeUtc");

        // EventDownload configuration - many-to-many junction table
        modelBuilder.Entity<EventDownload>()
            .HasIndex(ed => new { ed.EventId, ed.DownloadId })
            .HasDatabaseName("IX_EventDownloads_EventId_DownloadId")
            .IsUnique();

        modelBuilder.Entity<EventDownload>()
            .HasIndex(ed => ed.DownloadId)
            .HasDatabaseName("IX_EventDownloads_DownloadId");

        modelBuilder.Entity<EventDownload>()
            .HasIndex(ed => ed.TaggedAtUtc)
            .HasDatabaseName("IX_EventDownloads_TaggedAtUtc");

        // Configure relationships
        modelBuilder.Entity<EventDownload>()
            .HasOne(ed => ed.Event)
            .WithMany(e => e.EventDownloads)
            .HasForeignKey(ed => ed.EventId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<EventDownload>()
            .HasOne(ed => ed.Download)
            .WithMany()
            .HasForeignKey(ed => ed.DownloadId)
            .OnDelete(DeleteBehavior.Cascade);

        // ClientGroup configuration
        modelBuilder.Entity<ClientGroup>()
            .HasIndex(cg => cg.Nickname)
            .HasDatabaseName("IX_ClientGroups_Nickname")
            .IsUnique();

        // ClientGroupMember configuration
        modelBuilder.Entity<ClientGroupMember>()
            .HasIndex(cgm => cgm.ClientIp)
            .HasDatabaseName("IX_ClientGroupMembers_ClientIp")
            .IsUnique(); // One IP can only belong to one group

        modelBuilder.Entity<ClientGroupMember>()
            .HasIndex(cgm => cgm.ClientGroupId)
            .HasDatabaseName("IX_ClientGroupMembers_ClientGroupId");

        // Configure one-to-many relationship
        modelBuilder.Entity<ClientGroupMember>()
            .HasOne(cgm => cgm.ClientGroup)
            .WithMany(cg => cg.Members)
            .HasForeignKey(cgm => cgm.ClientGroupId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}