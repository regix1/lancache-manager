import { useEffect, useState } from 'react';
import { APP_EVENTS } from '@utils/constants';

export function getThemeColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/**
 * A corner token as a plain pixel number, for the charts. Canvas marks cannot
 * read CSS, so a chart drew its own hard-coded corners and kept them when the
 * theme asked for sharp ones. The tokens are in rem, so they resolve against
 * the root font size. The theme's style element is applied from an async
 * effect and replaced outright on every theme change, so a chart can render
 * while the tokens are missing; returning 0 there keeps NaN out of Chart.js.
 */
export function getThemeRadius(name: string): number {
  const token = getThemeColor(name);
  const value = Number.parseFloat(token);
  if (!Number.isFinite(value)) return 0;

  const rootFontSize = Number.parseFloat(getComputedStyle(document.documentElement).fontSize);
  return token.endsWith('rem') ? value * rootFontSize : value;
}

export function useThemeRevision(): number {
  const [revision, setRevision] = useState(0);

  useEffect(() => {
    const updateRevision = () => setRevision((current) => current + 1);
    window.addEventListener(APP_EVENTS.THEME_CHANGE, updateRevision);
    return () => window.removeEventListener(APP_EVENTS.THEME_CHANGE, updateRevision);
  }, []);

  return revision;
}
