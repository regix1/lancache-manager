namespace LancacheManager.Models;

public class ManagementAction
{
    public string Action { get; set; } = string.Empty;
    public string? Service { get; set; }
    public string? Parameters { get; set; }
}