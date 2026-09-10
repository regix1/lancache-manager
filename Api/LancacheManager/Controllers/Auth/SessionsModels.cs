namespace LancacheManager.Controllers;

public class RefreshRateRequest
{
    public string? RefreshRate { get; set; }
}

public class ClientInfoRequest
{
    public string? Timezone { get; set; }
    public string? Language { get; set; }
    public string? ScreenResolution { get; set; }
}
