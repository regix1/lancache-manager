/**
 * Client filter dropdown options shared by the downloads tab and the dashboard's
 * recent downloads panel. Both surfaces offer the same choice of client, so the
 * grouping and ordering rules live here rather than being spelled out twice and
 * drifting apart.
 */

import type { ClientGroup } from '../types';
import { resolveClientLabel } from './clientLabel';

/** One entry of the client filter dropdown. */
interface ClientFilterOption {
  value: string;
  label: string;
  description?: string;
}

/**
 * Collapses the observed client IPs into one entry per named group (member IPs
 * listed in the description), sorted by nickname, followed by the ungrouped IPs
 * sorted individually. `allClientsLabel` is passed in because each surface has
 * its own translation for the leading "all clients" entry.
 *
 * An ungrouped client reads the same label as the rows it filters, so a hostname
 * shows here too rather than the dropdown disagreeing with the list below it. A
 * group spans several addresses and so has no hostname of its own.
 */
export const buildClientFilterOptions = (
  availableClients: string[],
  getGroupForIp: (clientIp: string) => ClientGroup | null,
  getHostnameForIp: (clientIp: string) => string | null,
  allClientsLabel: string
): ClientFilterOption[] => {
  const groupedIps = new Map<number, { group: ClientGroup; ips: string[] }>();
  const ungroupedIps: string[] = [];

  availableClients.forEach((clientIp) => {
    const group = getGroupForIp(clientIp);
    if (group && group.nickname) {
      const existing = groupedIps.get(group.id);
      if (existing) {
        existing.ips.push(clientIp);
      } else {
        groupedIps.set(group.id, { group, ips: [clientIp] });
      }
    } else {
      ungroupedIps.push(clientIp);
    }
  });

  const options: ClientFilterOption[] = [{ value: 'all', label: allClientsLabel }];

  Array.from(groupedIps.values())
    .sort((a, b) => a.group.nickname.localeCompare(b.group.nickname))
    .forEach(({ group, ips }) => {
      options.push({
        value: `group-${group.id}`,
        label: group.nickname,
        description: ips.join(', ')
      });
    });

  ungroupedIps.sort().forEach((ip) => {
    const { text, substitutesAddress } = resolveClientLabel(ip, null, getHostnameForIp(ip));
    options.push({
      value: ip,
      label: text,
      description: substitutesAddress ? ip : undefined
    });
  });

  return options;
};

/**
 * Reads a selected filter value back as the group it names, or null when it names no group. Lives
 * beside the option builder that mints the value, so the two halves of the format cannot drift.
 *
 * A selection outlives the group it points at: it is persisted, so it survives the group being
 * renamed away or deleted, and only the leading "all" and the plain addresses are guaranteed to
 * still mean something. Anything that is not a group id resolves to null rather than to a NaN that
 * silently matches nothing.
 */
export const findClientFilterGroup = (
  filterValue: string,
  clientGroups: ClientGroup[]
): ClientGroup | null => {
  const prefix = 'group-';
  if (!filterValue.startsWith(prefix)) return null;

  const groupId = Number(filterValue.slice(prefix.length));
  if (Number.isNaN(groupId)) return null;

  return clientGroups.find((group) => group.id === groupId) ?? null;
};
