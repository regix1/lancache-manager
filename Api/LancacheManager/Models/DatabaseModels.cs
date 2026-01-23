namespace LancacheManager.Models;

public class ResetProgressInfo
{
    public bool IsProcessing { get; set; }
    public double PercentComplete { get; set; }
    public string Message { get; set; } = "";
    public string Status { get; set; } = "idle";
}
