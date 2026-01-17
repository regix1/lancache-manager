namespace LancacheManager.Models;

public static class ClientExclusionModes
{
    public const string Hide = "hide";
    public const string Exclude = "exclude";
}

public class ClientExclusionRule
{
    public string Ip { get; set; } = string.Empty;
    public string Mode { get; set; } = ClientExclusionModes.Hide;
}
