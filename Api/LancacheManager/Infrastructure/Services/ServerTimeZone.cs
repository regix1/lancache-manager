using System.Collections.Concurrent;
using LancacheManager.Infrastructure.Services.Scheduling;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// The zone the server reports as its own, for every reader that needs to name it: the TZ environment
/// variable (the Docker standard) or the TimeZone setting when either names a zone this server can
/// actually resolve, and otherwise the machine's OWN zone. A flat "UTC" fallback is only true of a container started without
/// TZ - it told a bare-metal install in New York that its server ran on UTC. The id always comes back in
/// IANA form because that is what the browser and the schedule validator both read; a Windows machine
/// names its zones differently and is translated here rather than at each reader. [13]
/// </summary>
public static class ServerTimeZone
{
    // One answer per distinct configured id. Settling a name costs an exception when the runtime does not
    // have that zone, and this is asked on every status read and by every reader that formats a time, so a
    // name nothing resolves would otherwise pay that cost thousands of times over one long schedule
    // preview. Keyed on the configured text rather than held as a single answer, so two differently
    // configured readers cannot be handed each other's zone. [11]
    private static readonly ConcurrentDictionary<string, string?> _configuredZones =
        new(StringComparer.OrdinalIgnoreCase);

    public static string IanaId(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration.GetValue<string>("TZ") ?? configuration.GetValue<string>("TimeZone");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var named = _configuredZones.GetOrAdd(configured.Trim(), ResolvedConfiguredId);
            if (named is not null)
            {
                return named;
            }
        }

        return MachineIanaId();
    }

    /// <summary>
    /// The IANA spelling of a configured zone id, or null when this runtime cannot find that zone at all.
    /// A name nothing resolves is worse than no name: it reaches every reader that formats a time and
    /// throws there instead, once per formatted instant. Settling it here means an unknown zone falls back
    /// to the machine's own zone once, deterministically, rather than failing again at each reader. [11]
    /// </summary>
    private static string? ResolvedConfiguredId(string zoneId)
    {
        // A configured id we cannot translate is kept as written: it is far more likely to be an IANA
        // name this runtime simply does not map than a Windows name we should have caught. [12]
        var named = IanaId(zoneId, zoneId);
        return ScheduleTiming.ResolveTimeZone(named) is null ? null : named;
    }

    /// <summary>
    /// The zone the machine itself runs on, in IANA form. Also what an unusable configured id falls back
    /// to, because a name this server cannot resolve is worth no more than no name at all.
    /// </summary>
    private static string MachineIanaId()
    {
        var local = TimeZoneInfo.Local;
        if (local.HasIanaId)
        {
            return local.Id;
        }

        // Nobody asked for this zone, so an untranslatable machine id is worth less than a name every
        // reader can parse.
        return IanaId(local.Id, "UTC");
    }

    /// <summary>
    /// The IANA spelling of one zone id, falling back to <paramref name="whenUntranslatable"/> when this
    /// runtime cannot map it. Both the configured zone and the machine's own zone come through here so
    /// they cannot end up disagreeing about how a zone is named. [1]
    /// </summary>
    public static string IanaId(string zoneId, string whenUntranslatable)
    {
        // UTC is spelled the same in both worlds and every reader parses it, but the translation
        // renames it to "Etc/UTC". A schedule stores the zone it was saved with and is compared to
        // this id by name, so the rename would make every schedule saved on a UTC server read as
        // overriding a zone it names exactly. [65]
        if (string.Equals(zoneId, "UTC", StringComparison.OrdinalIgnoreCase))
        {
            return "UTC";
        }

        return TimeZoneInfo.TryConvertWindowsIdToIanaId(zoneId, out var ianaId) ? ianaId : whenUntranslatable;
    }
}
