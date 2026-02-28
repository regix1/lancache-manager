import { useCallback, useEffect, useState } from 'react';

const SERVICE_COLOR_MAP: Record<string, string> = {
  steam: '--theme-steam',
  epic: '--theme-epic',
  origin: '--theme-origin',
  ea: '--theme-origin',
  blizzard: '--theme-blizzard',
  'battle.net': '--theme-blizzard',
  battlenet: '--theme-blizzard',
  wsus: '--theme-wsus',
  windows: '--theme-wsus',
  riot: '--theme-riot',
  riotgames: '--theme-riot',
  xbox: '--theme-xbox',
  xboxlive: '--theme-xbox',
  ubisoft: '--theme-ubisoft',
  uplay: '--theme-ubisoft',
  gog: '--theme-text-secondary',
  rockstar: '--theme-warning'
};

interface ServiceColors {
  getColor: (serviceName: string) => string;
  getCacheHitColor: () => string;
  getCacheMissColor: () => string;
  getBorderColor: () => string;
  isReady: boolean;
}

export function useServiceColors(): ServiceColors {
  const [colors, setColors] = useState<{
    serviceColors: Map<string, string>;
    cacheHit: string;
    cacheMiss: string;
    border: string;
  }>({
    serviceColors: new Map(),
    cacheHit: '#22c55e',
    cacheMiss: '#ef4444',
    border: '#1a1a2e'
  });
  const [isReady, setIsReady] = useState(false);

  useEffect(() => {
    const resolveColors = () => {
      const computed = getComputedStyle(document.documentElement);
      const newServiceColors = new Map<string, string>();

      // Resolve service colors
      Object.entries(SERVICE_COLOR_MAP).forEach(([service, cssVar]) => {
        const color = computed.getPropertyValue(cssVar).trim();
        newServiceColors.set(service, color || '#888888');
      });

      // Resolve chart-specific colors
      const cacheHit = computed.getPropertyValue('--theme-chart-cache-hit').trim() || '#22c55e';
      const cacheMiss = computed.getPropertyValue('--theme-chart-cache-miss').trim() || '#ef4444';
      const border = computed.getPropertyValue('--theme-chart-border').trim() || '#1a1a2e';

      setColors({
        serviceColors: newServiceColors,
        cacheHit,
        cacheMiss,
        border
      });
      setIsReady(true);
    };

    // Initial resolution
    resolveColors();

    // Listen for theme changes
    window.addEventListener('themechange', resolveColors);

    return () => {
      window.removeEventListener('themechange', resolveColors);
    };
  }, []);

  const getColor = useCallback(
    (serviceName: string): string => {
      const normalizedName = serviceName.toLowerCase();
      return colors.serviceColors.get(normalizedName) || '#888888';
    },
    [colors.serviceColors]
  );

  const getCacheHitColor = useCallback(() => colors.cacheHit, [colors.cacheHit]);
  const getCacheMissColor = useCallback(() => colors.cacheMiss, [colors.cacheMiss]);
  const getBorderColor = useCallback(() => colors.border, [colors.border]);

  return {
    getColor,
    getCacheHitColor,
    getCacheMissColor,
    getBorderColor,
    isReady
  };
}
