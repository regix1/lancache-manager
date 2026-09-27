import { useLayoutEffect, useMemo, useState } from 'react';
import type { Download } from '../../../types';

export const useIpExpansion = (downloads: Download[], membersReady: boolean) => {
  const [expandedIps, setExpandedIps] = useState<Record<string, boolean>>({});
  const memberCounts = useMemo(() => {
    const counts = new Map<string, number>();
    downloads.forEach((download) => {
      counts.set(download.clientIp, (counts.get(download.clientIp) ?? 0) + 1);
    });
    return counts;
  }, [downloads]);

  useLayoutEffect(() => {
    if (!membersReady) return;

    setExpandedIps((previous) => {
      let next = previous;
      memberCounts.forEach((count, ip) => {
        if (ip in previous) return;
        if (next === previous) next = { ...previous };
        next[ip] = count <= 5;
      });
      return next;
    });
  }, [memberCounts, membersReady]);

  const toggleIp = (ip: string, visibleCount: number): void => {
    setExpandedIps((previous) => {
      const displayed =
        ip in previous ? previous[ip] : membersReady && (memberCounts.get(ip) ?? visibleCount) <= 5;
      return { ...previous, [ip]: !displayed };
    });
  };

  const isIpExpanded = (ip: string, count: number): boolean => {
    if (ip in expandedIps) return expandedIps[ip];
    return membersReady && (memberCounts.get(ip) ?? count) <= 5;
  };

  return { toggleIp, isIpExpanded };
};
