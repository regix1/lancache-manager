namespace LancacheManager.Models;

public static class EvictedDataModes
{
    public const string Show = "show";           // visible with badge + dimmed, included in stats
    public const string ShowClean = "showClean"; // visible without badge or dimming, included in stats
    public const string Hide = "hide";           // hidden from all pages + excluded from stats
    public const string Remove = "remove";       // deleted from DB by reconciliation
}
