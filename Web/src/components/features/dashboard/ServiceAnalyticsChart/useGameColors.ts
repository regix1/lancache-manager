import { useCallback, useEffect, useState } from 'react';

const GAME_COLOR_VARS = Array.from({ length: 20 }, (_, i) => `--theme-game-${i + 1}`);
const GAME_OTHER_VAR = '--theme-game-other';

interface GameColors {
  getGameColors: (count: number) => string[];
  getOtherColor: () => string;
  isReady: boolean;
}

export function useGameColors(): GameColors {
  const [colors, setColors] = useState<string[]>([]);
  const [otherColor, setOtherColor] = useState('');
  const [isReady, setIsReady] = useState(false);

  useEffect(() => {
    const resolveColors = () => {
      const computed = getComputedStyle(document.documentElement);
      const resolved = GAME_COLOR_VARS.map((cssVar) =>
        computed.getPropertyValue(cssVar).trim()
      ).filter(Boolean);

      const other = computed.getPropertyValue(GAME_OTHER_VAR).trim();

      setColors(resolved);
      setOtherColor(other);
      setIsReady(true);
    };

    resolveColors();
    window.addEventListener('themechange', resolveColors);
    return () => window.removeEventListener('themechange', resolveColors);
  }, []);

  const getGameColors = useCallback(
    (count: number): string[] => {
      if (count <= 0) return [];
      return colors.slice(0, Math.min(count, colors.length));
    },
    [colors]
  );

  const getOtherColor = useCallback(() => otherColor, [otherColor]);

  return { getGameColors, getOtherColor, isReady };
}
