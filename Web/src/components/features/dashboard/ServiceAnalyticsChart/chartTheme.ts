import { useEffect, useState } from 'react';
import { APP_EVENTS } from '@utils/constants';
import { formatBytes } from '@utils/formatters';

export function getThemeColor(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

/** The app's sans face for canvas text, which cannot read CSS. Without it Chart.js draws its own
 *  Helvetica/Arial stack, so axis and legend text sat in a different face from the card around it. */
export function getChartFontFamily(): string {
  return getThemeColor('--font-sans');
}

/**
 * A byte-axis tick step that is a power of two, so every tick is a whole binary size (16 GB,
 * 512 GB, 1.5 TB). Chart.js picks decimal steps, which the binary formatter prints as 9.31 GB,
 * 18.63 GB and so on. The step leaves at most `gaps` gaps between zero and `maxBytes`.
 */
export function byteAxisStep(maxBytes: number, gaps: number): number {
  return 2 ** Math.max(0, Math.ceil(Math.log2(maxBytes / gaps)));
}

/** A byte axis tick without the trailing zeros formatBytes keeps for its two decimals. */
export function formatAxisBytes(bytes: number): string {
  const [amount, unit] = formatBytes(bytes).split(' ');
  return `${Number(amount)} ${unit}`;
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
