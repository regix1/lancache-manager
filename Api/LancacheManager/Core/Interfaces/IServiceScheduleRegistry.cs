using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IServiceScheduleRegistry
{
    IReadOnlyList<ServiceScheduleInfo> GetAll();
    ServiceScheduleInfo? Get(string serviceKey);
    void SetInterval(string serviceKey, double intervalHours);
    void SetRunOnStartup(string serviceKey, bool runOnStartup);
    Task TriggerRunAsync(string serviceKey);
    void ResetToDefaults();
}
