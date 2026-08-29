using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Resolves game names (and Steam app ids) on rows that were ingested before their mapping
/// existed. One shared implementation: the dashboard batch and the retro endpoint used to carry
/// diverging copies, and the retro copy missed the multi-owner depot fix, crashing its lookup on
/// the duplicate key and silently returning an empty page.
/// </summary>
public static class GameNameResolver
{
    /// <summary>
    /// Priority: existing GameName -> Steam AppName -> Epic Name -> Xbox Title -> Service.
    /// </summary>
    public static async Task ResolveAsync(
        AppDbContext context,
        IReadOnlyList<IGameNameRow> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0) return;

        var depotIds = rows
            .Where(r => r.DepotId.HasValue)
            .Select(r => r.DepotId!.Value)
            .Distinct()
            .ToList();

        var steamMappingRows = depotIds.Count > 0
            ? await context.SteamDepotMappings
                .AsNoTracking()
                .Where(m => m.IsOwner && depotIds.Contains(m.DepotId))
                .ToListAsync(ct)
            : [];
        // A depot can carry more than one owner row (prefill used to claim shared and DLC
        // depots), so the winner is picked per depot instead of keyed directly.
        var steamMappings = steamMappingRows
            .GroupBy(m => m.DepotId)
            .ToDictionary(
                group => group.Key,
                SteamDepotMapping.SelectOwner);

        var epicAppIds = rows
            .Where(r => !string.IsNullOrEmpty(r.EpicAppId))
            .Select(r => r.EpicAppId!)
            .Distinct()
            .ToList();

        var epicMappings = epicAppIds.Count > 0
            ? await context.EpicGameMappings
                .AsNoTracking()
                .Where(m => epicAppIds.Contains(m.AppId))
                .ToDictionaryAsync(m => m.AppId, m => m.Name, ct)
            : new Dictionary<string, string>();

        var xboxProductIds = rows
            .Where(r => !string.IsNullOrEmpty(r.XboxProductId))
            .Select(r => r.XboxProductId!)
            .Distinct()
            .ToList();

        var xboxMappings = xboxProductIds.Count > 0
            ? await context.XboxGameMappings
                .AsNoTracking()
                .Where(m => xboxProductIds.Contains(m.ProductId))
                .ToDictionaryAsync(m => m.ProductId, m => m.Title, ct)
            : new Dictionary<string, string>();

        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.GameName) && r.DepotId.HasValue
                && steamMappings.TryGetValue(r.DepotId.Value, out var steamMapping))
            {
                r.GameName = steamMapping.AppName;
                r.GameAppId = steamMapping.AppId;
            }

            if (string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.EpicAppId)
                && epicMappings.TryGetValue(r.EpicAppId, out var epicName))
            {
                r.GameName = epicName;
            }

            if (string.IsNullOrEmpty(r.GameName) && !string.IsNullOrEmpty(r.XboxProductId)
                && xboxMappings.TryGetValue(r.XboxProductId, out var xboxTitle))
            {
                r.GameName = xboxTitle;
            }

            if (string.IsNullOrEmpty(r.GameName))
            {
                r.GameName = r.Service;
            }
        }
    }
}
