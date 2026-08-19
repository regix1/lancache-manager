namespace LancacheManager.Core.Services;

public partial class GameCacheDetectionService
{
    public async Task RemoveGameFromCacheAsync(long gameAppId)
    {
        await _detectionDataService.RemoveGameFromCacheAsync(gameAppId);
        InvalidateDetectionCache();
    }

    public async Task RemoveServiceFromCacheAsync(string serviceName)
    {
        await _detectionDataService.RemoveServiceFromCacheAsync(serviceName);
        InvalidateDetectionCache();
    }

    public async Task RemoveNamedGameFromCacheAsync(string service, string gameName)
    {
        await _detectionDataService.RemoveNamedGameFromCacheAsync(service, gameName);
        InvalidateDetectionCache();
    }

    public async Task RemoveEpicGameFromCacheAsync(string gameName)
    {
        await _detectionDataService.RemoveEpicGameFromCacheAsync(gameName);
        InvalidateDetectionCache();
    }
}
