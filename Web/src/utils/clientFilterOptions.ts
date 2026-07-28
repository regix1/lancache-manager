/**
 * Client filter dropdown options shared by the downloads tab and the dashboard's
 * recent downloads panel. Both surfaces offer the same choice of client, so the
 * grouping and ordering rules live here rather than being spelled out twice and
 * drifting apart.
 */

import type { ClientGroup } from '../types';

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
 */
export const buildClientFilterOptions = (
  availableClients: string[],
  getGroupForIp: (clientIp: string) => ClientGroup | null,
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
    options.push({
      value: ip,
      label: ip
    });
  });

  return options;
};
