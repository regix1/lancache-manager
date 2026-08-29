namespace LancacheManager.Core.Interfaces;

/// <summary>
/// A row whose game identity can be filled in from the mapping tables by GameNameResolver: a raw
/// download (dashboard/downloads lists) or a retro depot group.
/// </summary>
public interface IGameNameRow
{
    long? DepotId { get; }
    string? EpicAppId { get; }
    string? XboxProductId { get; }
    string Service { get; }
    string? GameName { get; set; }
    long? GameAppId { get; set; }
}
