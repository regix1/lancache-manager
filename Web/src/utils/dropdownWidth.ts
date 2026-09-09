const DROPDOWN_MAX_WIDTH_MARGIN_PX = 32; // Matches `max-w-[calc(100vw-32px)]`

function getRootFontSizePx(): number {
  const fontSize = window.getComputedStyle(document.documentElement).fontSize;
  const parsed = Number.parseFloat(fontSize);
  return Number.isFinite(parsed) ? parsed : 16;
}

function resolveCssWidthToPx(value: string, fallbackPx: number, rootFontSizePx: number): number {
  const trimmed = value.trim();

  const pxMatch = trimmed.match(/^(\d+(?:\.\d+)?)px$/);
  if (pxMatch) return Number.parseFloat(pxMatch[1]);

  const remMatch = trimmed.match(/^(\d+(?:\.\d+)?)rem$/);
  if (remMatch) return Number.parseFloat(remMatch[1]) * rootFontSizePx;

  const percentMatch = trimmed.match(/^(\d+(?:\.\d+)?)%$/);
  if (percentMatch) return (Number.parseFloat(percentMatch[1]) / 100) * window.innerWidth;

  const vwMatch = trimmed.match(/^(\d+(?:\.\d+)?)vw$/);
  if (vwMatch) return (Number.parseFloat(vwMatch[1]) / 100) * window.innerWidth;

  const numeric = Number.parseFloat(trimmed);
  if (Number.isFinite(numeric)) return numeric;

  return fallbackPx;
}

export function resolveDropdownWidthToPx(
  dropdownWidth: string | undefined,
  fallbackPx: number
): number {
  if (!dropdownWidth) return fallbackPx;

  const widthToken = dropdownWidth
    .trim()
    .split(/\s+/)
    .find((token) => token.startsWith('w-'));

  if (!widthToken) {
    // Treat as CSS width value (e.g. "280px", "18rem")
    return resolveCssWidthToPx(dropdownWidth, fallbackPx, getRootFontSizePx());
  }

  // Tailwind width classes (common cases used in this app)
  if (widthToken === 'w-full' || widthToken === 'w-screen') {
    return Math.max(fallbackPx, window.innerWidth - DROPDOWN_MAX_WIDTH_MARGIN_PX);
  }

  const bracketMatch = widthToken.match(/^w-\[(.+)\]$/);
  if (bracketMatch) {
    return resolveCssWidthToPx(bracketMatch[1], fallbackPx, getRootFontSizePx());
  }

  const numericMatch = widthToken.match(/^w-(\d+)$/);
  if (numericMatch) {
    const scale = Number.parseInt(numericMatch[1], 10);
    if (Number.isFinite(scale)) {
      // Tailwind spacing scale: 1 = 0.25rem
      return scale * (getRootFontSizePx() / 4);
    }
  }

  return fallbackPx;
}
