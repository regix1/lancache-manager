namespace LancacheManager.Models;

public class ServiceScheduleInfo
{
    public string Key { get; set; } = "";
    public double IntervalHours { get; set; }
    public bool RunOnStartup { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
}
