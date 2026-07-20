using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// Writable user settings for configured datasources.
/// </summary>
[ApiController]
[Route("api/system/datasources")]
[Authorize]
public class DatasourceConfigurationController : ControllerBase
{
    private readonly IStateService _stateService;
    private readonly DatasourceService _datasourceService;
    private readonly CacheManagementService _cacheManagementService;
    private readonly IDashboardBatchService _dashboardBatchService;

    public DatasourceConfigurationController(
        IStateService stateService,
        DatasourceService datasourceService,
        CacheManagementService cacheManagementService,
        IDashboardBatchService dashboardBatchService)
    {
        _stateService = stateService;
        _datasourceService = datasourceService;
        _cacheManagementService = cacheManagementService;
        _dashboardBatchService = dashboardBatchService;
    }

    /// <summary>
    /// Sets a datasource cache-size override. Null, blank, or zero restores automatic detection.
    /// </summary>
    [HttpPut("{datasourceName}/cache-size")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult<DatasourceCacheSizeResponse>> SetCacheSizeAsync(
        string datasourceName,
        [FromBody] SetDatasourceCacheSizeRequest request)
    {
        var overrideBytes = ResolveCacheSizeOverride(request);
        var datasource = _datasourceService.GetDatasource(datasourceName)
            ?? throw new NotFoundException("Datasource");

        _stateService.SetDatasourceCacheSizeOverride(datasource.Name, overrideBytes);
        _cacheManagementService.InvalidateConfiguredCacheSize();
        _dashboardBatchService.InvalidateLiveCache();

        // The resolution set is enabled datasources only. A disabled datasource is absent, so reflect
        // the value we just persisted instead of throwing, so setting a size on a disabled datasource
        // still round-trips.
        var resolution = (await _cacheManagementService.GetDatasourceCacheSizeResolutionsAsync())
            .FirstOrDefault(item => item.DatasourceName.Equals(datasource.Name, StringComparison.OrdinalIgnoreCase));

        var source = resolution?.Source
            ?? (overrideBytes.HasValue ? CacheSizeSource.Manual : CacheSizeSource.FullDisk);

        return Ok(new DatasourceCacheSizeResponse
        {
            Name = datasource.Name,
            CacheSizeOverrideBytes = resolution?.OverrideBytes ?? overrideBytes,
            ResolvedCacheSizeBytes = resolution?.ResolvedBytes ?? (overrideBytes ?? 0),
            CacheSizeSource = source.ToWireValue()
        });
    }

    internal static long? ResolveCacheSizeOverride(SetDatasourceCacheSizeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Size))
        {
            return null;
        }

        if (!CacheSizeParser.TryParse(request.Size, out var bytes))
        {
            throw new ValidationException(
                "Cache size must be a byte count or a size such as 2000g, 500G, 2t, or 1.5T.");
        }

        return bytes == 0 ? null : bytes;
    }
}
