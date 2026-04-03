import { useCallback, useEffect, useState } from 'react';

const CHART_COLOR_VARS = [
  '--theme-chart-1',
  '--theme-chart-2',
  '--theme-chart-3',
  '--theme-chart-4',
  '--theme-chart-5',
  '--theme-chart-6',
  '--theme-chart-7',
  '--theme-chart-8'
];

function hexToHsl(hex: string): [number, number, number] | null {
  const clean = hex.replace('#', '');
  if (clean.length !== 6) return null;
  const r = parseInt(clean.substring(0, 2), 16) / 255;
  const g = parseInt(clean.substring(2, 4), 16) / 255;
  const b = parseInt(clean.substring(4, 6), 16) / 255;

  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const l = (max + min) / 2;
  let h = 0;
  let s = 0;

  if (max !== min) {
    const d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    if (max === r) h = ((g - b) / d + (g < b ? 6 : 0)) / 6;
    else if (max === g) h = ((b - r) / d + 2) / 6;
    else h = ((r - g) / d + 4) / 6;
  }

  return [Math.round(h * 360), Math.round(s * 100), Math.round(l * 100)];
}

function generateExtraColors(startIndex: number, count: number): string[] {
  const colors: string[] = [];
  const goldenAngle = 137.508;

  for (let i = 0; i < count; i++) {
    const hue = ((startIndex + i) * goldenAngle) % 360;
    const saturation = 55 + (i % 3) * 10;
    const lightness = 45 + (i % 4) * 7;
    colors.push(`hsl(${Math.round(hue)}, ${saturation}%, ${lightness}%)`);
  }

  return colors;
}

interface GameColors {
  getGameColors: (count: number) => string[];
  getOtherColor: () => string;
  isReady: boolean;
}

export function useGameColors(): GameColors {
  const [baseColors, setBaseColors] = useState<string[]>([]);
  const [otherColor, setOtherColor] = useState('');
  const [isReady, setIsReady] = useState(false);

  useEffect(() => {
    const resolveColors = () => {
      const computed = getComputedStyle(document.documentElement);
      const resolved = CHART_COLOR_VARS.map((cssVar) =>
        computed.getPropertyValue(cssVar).trim()
      ).filter(Boolean);

      const muted = computed.getPropertyValue('--theme-text-muted').trim();

      setBaseColors(resolved);
      setOtherColor(muted);
      setIsReady(true);
    };

    resolveColors();
    window.addEventListener('themechange', resolveColors);
    return () => window.removeEventListener('themechange', resolveColors);
  }, []);

  const getGameColors = useCallback(
    (count: number): string[] => {
      if (count <= 0) return [];

      if (count <= baseColors.length) {
        return baseColors.slice(0, count);
      }

      const extraNeeded = count - baseColors.length;
      const startHueIndex = baseColors.reduce((acc, hex) => {
        const hsl = hexToHsl(hex);
        return hsl ? acc + hsl[0] : acc;
      }, 0);

      const extraColors = generateExtraColors(
        Math.round(startHueIndex / Math.max(baseColors.length, 1)),
        extraNeeded
      );

      return [...baseColors, ...extraColors];
    },
    [baseColors]
  );

  const getOtherColor = useCallback(() => otherColor, [otherColor]);

  return { getGameColors, getOtherColor, isReady };
}
