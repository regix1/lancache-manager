import { useState } from 'react';

export const useIpExpansion = () => {
  const [expandedIps, setExpandedIps] = useState<Record<string, boolean>>({});

  const toggleIp = (ip: string): void => {
    setExpandedIps((previous: Record<string, boolean>) => ({
      ...previous,
      [ip]: !previous[ip]
    }));
  };

  const isIpExpanded = (ip: string, count: number): boolean => {
    if (ip in expandedIps) return expandedIps[ip];
    return count <= 5;
  };

  return { toggleIp, isIpExpanded };
};
