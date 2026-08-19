namespace LancacheManager.Models;

public class EventCompareResponse
{
    public int BucketMinutes { get; set; }
    public List<int> ElapsedMinutes { get; set; } = [];
    public List<EventCompareSeries> Series { get; set; } = [];
}

public class EventCompareSeries
{
    public long EventId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ColorIndex { get; set; }
    public List<double?> Served { get; set; } = [];
    public List<double?> Saved { get; set; } = [];
}
