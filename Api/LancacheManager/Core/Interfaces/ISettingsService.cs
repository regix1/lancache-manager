using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface ISettingsService
{
    GcSettings GetSettings();
    Task<GcSettings> UpdateSettingsAsync(GcSettings newSettings);
    (long thresholdBytes, TimeSpan minTimeBetweenChecks, bool onPageLoadOnly, bool disabled) GetComputedSettings();
}
