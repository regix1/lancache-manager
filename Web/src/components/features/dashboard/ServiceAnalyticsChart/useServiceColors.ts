import { useCallback, useEffect, useState } from 'react';

const SERVICE_COLOR_MAP: Record<string, string> = {
  steam: '--theme-steam',
  epic: '--theme-epic',
  epicgames: '--theme-epic',
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
    cacheHit: '',
    cacheMiss: '',
    border: ''
  });
  const [isReady, setIsReady] = useState(false);

  useEffect(() => {
    const resolveColors = () => {
      const computed = getComputedStyle(document.documentElement);
      const newServiceColors = new Map<string, string>();

      // Resolve service colors from CSS custom properties
      Object.entries(SERVICE_COLOR_MAP).forEach(([service, cssVar]) => {
        const color = computed.getPropertyValue(cssVar).trim();
        newServiceColors.set(service, color);
      });

      // Resolve chart-specific colors from CSS custom properties
      const cacheHit = computed.getPropertyValue('--theme-chart-cache-hit').trim();
      const cacheMiss = computed.getPropertyValue('--theme-chart-cache-miss').trim();
      const border = computed.getPropertyValue('--theme-chart-border').trim();

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
      return (
        colors.serviceColors.get(normalizedName) ||
        getComputedStyle(document.documentElement).getPropertyValue('--theme-text-secondary').trim()
      );
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
