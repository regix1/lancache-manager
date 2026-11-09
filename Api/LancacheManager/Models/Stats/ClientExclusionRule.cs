using System.Text.Json.Serialization;

namespace LancacheManager.Models;

public static class ClientExclusionModes
{
    public const string Hide = "hide";
    public const string Exclude = "exclude";
}

public static class ClientExclusionTraffic
{
    public const string All = "all";
    public const string Prefill = "prefill";
}

public class ClientExclusionRule
{
    /// <summary>Empty only for a prefill rule, where it matches every address.</summary>
    public string Ip { get; set; } = string.Empty;
    public string Mode { get; set; } = ClientExclusionModes.Hide;
    public string Traffic { get; set; } = ClientExclusionTraffic.All;
    /// <summary>Empty matches every service.</summary>
    public List<PrefillPlatform> Services { get; set; } = [];

    /// <summary>
    /// Identity used to dedupe rules and to key caches: mode, traffic, address and the service
    /// names sorted ordinally, joined with '|' (services joined with ','), which no address or
    /// service name contains. The browser builds the same key.
    /// </summary>
    [JsonIgnore]
    public string RuleKey => string.Join('|', Mode, Traffic, Ip,
        string.Join(',', Services.Select(service => service.ToString()).Order(StringComparer.Ordinal)));
}
