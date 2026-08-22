import { useCallback, useEffect, useState } from 'react';
import { APP_EVENTS } from '@utils/constants';
import { SERVICE_COLOR_VARS, UNKNOWN_COLOR_VAR, getServiceColorVar } from '@utils/serviceColors';

interface ServiceColors {
  getColor: (serviceName: string) => string;
  getCacheHitColor: () => string;
  getCacheMissColor: () => string;
  getBorderColor: () => string;
}

export function useServiceColors(): ServiceColors {
  const [colors, setColors] = useState<{
    colorByVar: Map<string, string>;
    cacheHit: string;
    cacheMiss: string;
    border: string;
  }>({
    colorByVar: new Map(),
    cacheHit: '',
    cacheMiss: '',
    border: ''
  });

  useEffect(() => {
    const resolveColors = () => {
      const computed = getComputedStyle(document.documentElement);
      const newColorByVar = new Map<string, string>();

      // Resolve every brand color once per theme change, plus the muted text color a
      // service without a brand color of its own falls back to.
      [...SERVICE_COLOR_VARS, UNKNOWN_COLOR_VAR].forEach((cssVar) => {
        newColorByVar.set(cssVar, computed.getPropertyValue(cssVar).trim());
      });

      // Resolve chart-specific colors from CSS custom properties
      const cacheHit = computed.getPropertyValue('--theme-chart-cache-hit').trim();
      const cacheMiss = computed.getPropertyValue('--theme-chart-cache-miss').trim();
      const border = computed.getPropertyValue('--theme-chart-border').trim();

      setColors({
        colorByVar: newColorByVar,
        cacheHit,
        cacheMiss,
        border
      });
    };

    // Initial resolution
    resolveColors();

    // Listen for theme changes
    window.addEventListener(APP_EVENTS.THEME_CHANGE, resolveColors);

    return () => {
      window.removeEventListener(APP_EVENTS.THEME_CHANGE, resolveColors);
    };
  }, []);

  const getColor = useCallback(
    (serviceName: string): string => {
      const cssVar = getServiceColorVar(serviceName);
      return (
        colors.colorByVar.get(cssVar) ||
        getComputedStyle(document.documentElement).getPropertyValue(cssVar).trim()
      );
    },
    [colors.colorByVar]
  );

  const getCacheHitColor = useCallback(() => colors.cacheHit, [colors.cacheHit]);
  const getCacheMissColor = useCallback(() => colors.cacheMiss, [colors.cacheMiss]);
  const getBorderColor = useCallback(() => colors.border, [colors.border]);

  return {
    getColor,
    getCacheHitColor,
    getCacheMissColor,
    getBorderColor
  };
}
