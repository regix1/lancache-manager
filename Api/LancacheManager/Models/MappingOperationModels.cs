using LancacheManager.Hubs;

namespace LancacheManager.Models;

/// <summary>
/// Canonical identity, tracker type, SignalR event triple, and i18n stage-key prefix for a mapping
/// producer. Keeping these values together prevents a producer from mixing one platform's operation
/// type with another platform's event names.
/// </summary>
public sealed record MappingOperationDefinition(
    string ServiceKey,
    OperationType OperationType,
    ScheduledRunEventNames Events,
    string StageKeyPrefix);

/// <summary>
/// Closed set of mapping operations surfaced by the manager.
/// </summary>
public static class MappingOperations
{
    public static readonly MappingOperationDefinition Steam = new(
        "depotMapping",
        OperationType.DepotMapping,
        new ScheduledRunEventNames(
            SignalREvents.DepotMappingStarted,
            SignalREvents.DepotMappingProgress,
            SignalREvents.DepotMappingComplete),
        "signalr.depotMapping");

    public static readonly MappingOperationDefinition Epic = new(
        "epicMapping",
        OperationType.EpicMapping,
        new ScheduledRunEventNames(
            SignalREvents.EpicMappingStarted,
            SignalREvents.EpicMappingProgress,
            SignalREvents.EpicMappingComplete),
        "signalr.epicMapping");

    public static readonly MappingOperationDefinition Xbox = new(
        "xboxMapping",
        OperationType.XboxMapping,
        new ScheduledRunEventNames(
            SignalREvents.XboxMappingStarted,
            SignalREvents.XboxMappingProgress,
            SignalREvents.XboxMappingComplete),
        "signalr.xboxMapping");

    public static readonly MappingOperationDefinition BattleNet = new(
        "battleNetMapping",
        OperationType.BattleNetMapping,
        new ScheduledRunEventNames(
            SignalREvents.BattleNetMappingStarted,
            SignalREvents.BattleNetMappingProgress,
            SignalREvents.BattleNetMappingComplete),
        "signalr.battleNetMapping");

    public static readonly MappingOperationDefinition Riot = new(
        "riotMapping",
        OperationType.RiotMapping,
        new ScheduledRunEventNames(
            SignalREvents.RiotMappingStarted,
            SignalREvents.RiotMappingProgress,
            SignalREvents.RiotMappingComplete),
        "signalr.riotMapping");

    public static IReadOnlyList<MappingOperationDefinition> All { get; } =
        [Steam, Epic, Xbox, BattleNet, Riot];
}
