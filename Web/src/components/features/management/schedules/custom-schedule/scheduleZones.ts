import { canResolveTimezone, zoneOffsetMs } from '@utils/timezone';

/**
 * The zones a custom schedule can be read in. A schedule has always carried its own IANA zone
 * on the wire and the API validates it against the server's own zone database; until now the
 * modal only ever wrote the server's zone into it. This list is what lets a schedule say
 * "02:00 in Berlin" on a server that runs in UTC.
 */

/** A region of the zone database and the zones filed under it, both in display order. */
export interface ZoneRegion {
  /** The part before the first slash: "Europe", "America". */
  name: string;
  zones: string[];
}

const MINUTES_PER_HOUR = 60;
const MS_PER_MINUTE = 60_000;

/**
 * Every zone this browser knows. The lookup is newer than the project's TypeScript target, so
 * it is read off Intl by hand rather than assumed to be declared; a browser without it falls
 * back to the zones the modal offers on their own, which still covers the server and UTC.
 */
function supportedZones(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (key: string) => string[] };
  if (typeof intl.supportedValuesOf !== 'function') return [];
  try {
    return intl.supportedValuesOf('timeZone');
  } catch {
    return [];
  }
}

/** Worked out on the first call and kept: the zones a browser knows cannot change under it. */
let groupedZones: ZoneRegion[] | null = null;

/**
 * The zones grouped by their region prefix. Grouping four hundred ids and sorting each region
 * costs several milliseconds, and a schedule picker renders once per service row, so the answer
 * is computed once for the page rather than once per picker.
 */
export function listZoneRegions(): ZoneRegion[] {
  if (groupedZones) return groupedZones;
  const regions = new Map<string, string[]>();
  for (const zone of supportedZones()) {
    // An id with no region prefix has no region row to sit under. Every id this browser lists
    // has one, so this stands guard over a runtime whose list is shaped differently rather than
    // over a case seen here; UTC is offered at the top of the menu on its own account.
    const slash = zone.indexOf('/');
    if (slash < 0) continue;
    const name = zone.slice(0, slash);
    const zones = regions.get(name);
    if (zones) {
      zones.push(zone);
    } else {
      regions.set(name, [zone]);
    }
  }
  groupedZones = Array.from(
    regions,
    ([name, zones]): ZoneRegion => ({
      name,
      zones: zones.sort((left, right) => zoneCityLabel(left).localeCompare(zoneCityLabel(right)))
    })
  ).sort((left, right) => left.name.localeCompare(right.name));
  return groupedZones;
}

/**
 * The city half of a zone id, with underscores read as the spaces they stand for. Used to order
 * a region's zones: every id under one region carries the same prefix, so the part after it is
 * what a reader scans, and "New York" belongs beside its neighbours as two words rather than
 * wherever an underscore happens to sort. Not what the menu shows - a row spells the zone the
 * same way the trigger and the note beneath it do, which is the id itself.
 */
function zoneCityLabel(zone: string): string {
  const slash = zone.indexOf('/');
  return (slash < 0 ? zone : zone.slice(slash + 1)).replace(/_/g, ' ');
}

/**
 * How far a zone sits from UTC at a given instant, in minutes. Worked out by reading the zone's
 * own wall clock and subtracting the instant it belongs to, which is exact for the half-hour and
 * quarter-hour zones and for the ones past +12 that a wall-clock comparison alone cannot tell
 * apart from a negative offset.
 */
function zoneOffsetMinutes(zone: string, at: Date): number {
  return Math.round(zoneOffsetMs(at.getTime(), zone) / MS_PER_MINUTE);
}

/**
 * A zone's current offset written the way a schedule reader expects to see it, "UTC+01:00". It
 * is the current one rather than the standard one, so a zone presently on daylight saving time
 * reads as the clock the user would look at today.
 *
 * Null for an id this browser cannot resolve, which a schedule saved on another machine or an
 * older zone database can carry. Reading one is a thrown RangeError from the formatter itself,
 * and this runs in a render body, so the caller is handed an answer instead of an exception.
 */
export function zoneOffsetLabel(zone: string, at: Date): string | null {
  if (!canResolveTimezone(zone)) return null;
  const offset = zoneOffsetMinutes(zone, at);
  const sign = offset < 0 ? '-' : '+';
  const size = Math.abs(offset);
  const hours = Math.floor(size / MINUTES_PER_HOUR)
    .toString()
    .padStart(2, '0');
  const minutes = (size % MINUTES_PER_HOUR).toString().padStart(2, '0');
  return `UTC${sign}${hours}:${minutes}`;
}
