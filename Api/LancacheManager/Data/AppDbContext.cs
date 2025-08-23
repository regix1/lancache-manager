using Microsoft.EntityFrameworkCore;
using LancacheManager.Models;

namespace LancacheManager.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    
    public DbSet<Download> Downloads { get; set; }
    public DbSet<ClientStats> ClientStats { get; set; }
    public DbSet<ServiceStats> ServiceStats { get; set; }
    
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ClientStats>().HasKey(c => c.ClientIp);
        modelBuilder.Entity<ServiceStats>().HasKey(s => s.Service);
        modelBuilder.Entity<Download>().HasIndex(d => d.ClientIp);
        modelBuilder.Entity<Download>().HasIndex(d => d.Service);
        modelBuilder.Entity<Download>().HasIndex(d => d.StartTime);
    }
}