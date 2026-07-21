/**
 * Text measurement utilities using Canvas API
 * Used for calculating pixel-accurate text widths for dynamic column sizing
 *
 * Reference: https://developer.mozilla.org/en-US/docs/Web/API/CanvasRenderingContext2D/measureText
 */

// Cache the canvas context for performance
let cachedCanvas: HTMLCanvasElement | null = null;
let cachedContext: CanvasRenderingContext2D | null = null;

/**
 * Get or create a cached canvas context for text measurement
 */
function getCanvasContext(): CanvasRenderingContext2D | null {
  if (cachedContext) return cachedContext;

  if (typeof document === 'undefined') return null;

  cachedCanvas = document.createElement('canvas');
  cachedContext = cachedCanvas.getContext('2d');
  return cachedContext;
}

/**
 * Measure the pixel width of text using Canvas API
 * More accurate than DOM-based methods and doesn't affect global state
 *
 * @param text - The text to measure
 * @param font - CSS font string (e.g., "14px Inter, sans-serif")
 * @returns Width in pixels, or 0 if measurement fails
 */
export function measureTextWidth(text: string, font: string): number {
  const ctx = getCanvasContext();
  if (!ctx) return 0;

  ctx.font = font;
  const metrics = ctx.measureText(text);

  // CSS layout uses the text's advance width, while glyphs can sometimes
  // overhang that width. Keep whichever measurement is larger so fitted
  // columns do not clip trailing characters such as a timestamp's AM/PM.
  if (metrics.actualBoundingBoxLeft !== undefined && metrics.actualBoundingBoxRight !== undefined) {
    const glyphWidth =
      Math.abs(metrics.actualBoundingBoxLeft) + Math.abs(metrics.actualBoundingBoxRight);
    return Math.max(metrics.width, glyphWidth);
  }

  return metrics.width;
}

/** Column width model for the retro downloads table. */
export interface ColumnWidths {
  timestamp: number;
  banner: number;
  app: number;
  datasource: number;
  events: number;
  depot: number;
  client: number;
  speed: number;
  cacheHit: number;
  overall: number;
}

/** Font strings for each retro column, resolved against the app's real fonts. */
interface RetroViewFonts {
  header: string;
  timestamp: string;
  appName: string;
  badge: string;
  onDisk: string;
  datasource: string;
  depot: string;
  client: string;
  clientSub: string;
  speed: string;
  cacheValue: string;
  gaugeLabel: string;
}

// Matches Tailwind's font-mono stack, which the retro cells render with.
const MONO_FALLBACK =
  'ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, "Liberation Mono", "Courier New", monospace';
const SANS_FALLBACK = 'system-ui, -apple-system, sans-serif';

function resolveCssFontFamily(varName: string, fallback: string): string {
  if (typeof document === 'undefined') return fallback;
  const value = getComputedStyle(document.documentElement).getPropertyValue(varName).trim();
  return value || fallback;
}

let cachedRetroFonts: RetroViewFonts | null = null;

/**
 * Font definitions matching the RetroView cell styles. Canvas measurement must
 * use the fonts the browser actually renders with - the app body renders
 * `--font-sans` (Inter), not `system-ui`, and measuring with the wrong family
 * is what made auto-fit widths land short. Resolved once and cached.
 */
export function getRetroViewFonts(): RetroViewFonts {
  if (cachedRetroFonts) return cachedRetroFonts;

  const sans = resolveCssFontFamily('--font-sans', SANS_FALLBACK);
  const mono = MONO_FALLBACK;

  cachedRetroFonts = {
    // Header labels (uppercase mono, tracking-wider)
    header: `600 11px ${mono}`,
    // Timestamp lines (text-xs mono)
    timestamp: `400 12px ${mono}`,
    // App name (text-sm font-medium)
    appName: `500 14px ${sans}`,
    // Badge text inside the app cell (text-[11px] font-semibold)
    badge: `600 11px ${sans}`,
    // "X on disk" line under the app name (text-xs)
    onDisk: `400 12px ${sans}`,
    // Datasource badge (text-xs font-medium)
    datasource: `500 12px ${sans}`,
    // Depot ID (text-xs mono)
    depot: `400 12px ${mono}`,
    // Client IP / client count (text-xs mono)
    client: `400 12px ${mono}`,
    // Request-count sub-line under the client (10px mono)
    clientSub: `400 10px ${mono}`,
    // Speed (text-xs mono medium)
    speed: `500 12px ${mono}`,
    // Cache hit/miss labels under the bar (10px mono)
    cacheValue: `400 10px ${mono}`,
    // Efficiency gauge label under the dial (9px mono semibold uppercase)
    gaugeLabel: `600 9px ${mono}`
  };
  return cachedRetroFonts;
}
