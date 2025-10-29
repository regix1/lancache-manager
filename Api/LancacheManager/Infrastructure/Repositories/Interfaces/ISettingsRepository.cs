using LancacheManager.Application.DTOs;

namespace LancacheManager.Infrastructure.Repositories.Interfaces;

public interface ISettingsRepository
{
    GcSettings GetSettings();
    Task<GcSettings> UpdateSettingsAsync(GcSettings newSettings);
    (long thresholdBytes, TimeSpan minTimeBetweenChecks, bool onPageLoadOnly, bool disabled) GetComputedSettings();
}
