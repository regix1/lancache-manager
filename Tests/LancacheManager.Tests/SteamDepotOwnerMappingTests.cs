using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class SteamDepotOwnerMappingTests
{
    [Fact]
    public async Task PrefillMapping_DoesNotReplaceExistingPicsOwner()
    {
        var options = NewOptions();
        await using (var context = new AppDbContext(options))
        {
            context.SteamDepotMappings.Add(new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 1770480,
                AppName = "VTOL VR: AH-94",
                IsOwner = true,
                Source = "SteamKit2-PICS"
            });
            await context.SaveChangesAsync();
        }

        var service = new PrefillCacheService(
            new TestDbContextFactory(options),
            NullLogger<PrefillCacheService>.Instance);

        await service.RecordCachedDepotAsync(
            667970,
            1770481,
            4625979481897414804,
            "VTOL VR",
            1,
            null);

        await using var verification = new AppDbContext(options);
        var mappings = await verification.SteamDepotMappings
            .Where(mapping => mapping.DepotId == 1770481)
            .OrderBy(mapping => mapping.AppId)
            .ToListAsync();

        Assert.Equal(2, mappings.Count);
        Assert.Equal(1770480, Assert.Single(mappings, mapping => mapping.IsOwner).AppId);
        Assert.False(Assert.Single(mappings, mapping => mapping.AppId == 667970).IsOwner);
    }

    [Fact]
    public async Task DashboardNameEnrichment_ToleratesExistingDuplicateOwners()
    {
        var options = NewOptions();
        await using var context = new AppDbContext(options);
        context.SteamDepotMappings.AddRange(
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 1770480,
                AppName = "VTOL VR: AH-94",
                IsOwner = true,
                Source = "SteamKit2-PICS"
            },
            new SteamDepotMapping
            {
                DepotId = 1770481,
                AppId = 667970,
                AppName = "VTOL VR",
                IsOwner = true,
                Source = "Prefill"
            });
        await context.SaveChangesAsync();

        var downloads = new List<Download>
        {
            new()
            {
                DepotId = 1770481,
                Service = "steam"
            }
        };

        await DashboardBatchService.EnrichGameNamesAsync(context, downloads, CancellationToken.None);

        var download = Assert.Single(downloads);
        Assert.Equal(1770480, download.GameAppId);
        Assert.Equal("VTOL VR: AH-94", download.GameName);
    }

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"steam_depot_owner_{Guid.NewGuid():N}")
            .Options;
}
