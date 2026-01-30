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
function measureTextWidth(text: string, font: string): number {
  const ctx = getCanvasContext();
  if (!ctx) return 0;

  ctx.font = font;
  const metrics = ctx.measureText(text);

  // Use actualBoundingBox for more accurate measurements when available
  // This accounts for glyphs that extend beyond their advance width
  if (metrics.actualBoundingBoxLeft !== undefined && metrics.actualBoundingBoxRight !== undefined) {
    return Math.abs(metrics.actualBoundingBoxLeft) + Math.abs(metrics.actualBoundingBoxRight);
  }

  return metrics.width;
}

/**
 * Measure the maximum width needed for an array of text values
 *
 * @param texts - Array of text strings to measure
 * @param font - CSS font string
 * @param padding - Additional padding to add (default 16px for cell padding)
 * @returns Maximum width in pixels
 */
function measureMaxTextWidth(texts: string[], font: string, padding: number = 16): number {
  if (texts.length === 0) return 0;

  const maxWidth = texts.reduce((max, text) => {
    const width = measureTextWidth(text, font);
    return Math.max(max, width);
  }, 0);

  return Math.ceil(maxWidth + padding);
}

/**
 * Font definitions matching the RetroView component styles
 */
const RETRO_VIEW_FONTS = {
  // Header font (uppercase, semibold, tracking-wide)
  header: '600 11px system-ui, -apple-system, sans-serif',
  // Timestamp cell (text-xs)
  timestamp: '400 12px system-ui, -apple-system, sans-serif',
  // App name (text-sm font-medium)
  appName: '500 14px system-ui, -apple-system, sans-serif',
  // Depot ID (text-sm font-mono)
  depot: '400 14px ui-monospace, monospace',
  // Client IP (text-sm font-mono)
  client: '400 14px ui-monospace, monospace',
  // Speed (text-sm)
  speed: '400 14px system-ui, -apple-system, sans-serif',
  // Cache values (text-xs)
  cacheValue: '400 12px system-ui, -apple-system, sans-serif',
  // Overall percentage (text-lg font-bold)
  overall: '700 18px system-ui, -apple-system, sans-serif',
};

/**
 * Sample data patterns for calculating minimum column widths
 * Uses generic text patterns to estimate typical content widths
 */
const SAMPLE_DATA_PATTERNS = {
  // Timestamp patterns - longest format with full date on both sides including seconds
  timestamp: [
    'Dec 31, 2025, 10:05:19 PM - Dec 31, 2025, 10:05:48 PM',
  ],
  // App name patterns - use character width estimates (avg game name ~20 chars)
  // Using placeholder text to avoid hardcoding specific names
  appName: [
    'XXXXXXXXXXXXXXXXXX', // ~18 chars typical app name
  ],
  // Header labels
  headers: {
    timestamp: 'TIMESTAMP',
    app: 'APP',
    datasource: 'SOURCE',
    events: 'EVENTS',
    depot: 'DEPOT',
    client: 'CLIENT',
    speed: 'AVG SPEED',
    cacheHit: 'CACHE HIT',
    cacheMiss: 'CACHE MISS',
    overall: 'OVERALL',
  },
};

/**
 * Calculate optimal column widths based on content
 * Returns widths that ensure no truncation for typical data
 */
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
  cacheMiss: number;
  overall: number;
}

/**
 * Calculate smart minimum column widths based on:
 * 1. Header text width
 * 2. Typical content width (sample patterns)
 * 3. Minimum usable width
 *
 * @param actualData - Optional array of actual data to measure
 * @returns Calculated column widths
 */
export function calculateColumnWidths(actualData?: {
  timestamps?: string[];
  appNames?: string[];
  clientIps?: string[];
}): ColumnWidths {
  const CELL_PADDING = 16; // px padding for cells
  const RESIZE_HANDLE_WIDTH = 8; // px for resize handle
  const MIN_COLUMN_WIDTH = 60;

  // Measure header widths
  const headerWidths = {
    timestamp: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.timestamp, RETRO_VIEW_FONTS.header),
    app: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.app, RETRO_VIEW_FONTS.header),
    datasource: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.datasource, RETRO_VIEW_FONTS.header),
    events: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.events, RETRO_VIEW_FONTS.header),
    depot: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.depot, RETRO_VIEW_FONTS.header),
    client: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.client, RETRO_VIEW_FONTS.header),
    speed: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.speed, RETRO_VIEW_FONTS.header),
    cacheHit: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.cacheHit, RETRO_VIEW_FONTS.header),
    cacheMiss: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.cacheMiss, RETRO_VIEW_FONTS.header),
    overall: measureTextWidth(SAMPLE_DATA_PATTERNS.headers.overall, RETRO_VIEW_FONTS.header),
  };

  // Measure content widths from sample patterns
  const timestampSamples = actualData?.timestamps?.length
    ? actualData.timestamps
    : SAMPLE_DATA_PATTERNS.timestamp;
  const appNameSamples = actualData?.appNames?.length
    ? actualData.appNames
    : SAMPLE_DATA_PATTERNS.appName;

  // Timestamp: measure the longest timestamp pattern
  const timestampContentWidth = measureMaxTextWidth(timestampSamples, RETRO_VIEW_FONTS.timestamp, 0);

  // App: includes image (60-120px responsive) + gap (8px) + text
  // Use minimum responsive image width for default calculation
  const APP_IMAGE_WIDTH = 80;
  const APP_GAP = 8;
  const appNameContentWidth = measureMaxTextWidth(appNameSamples, RETRO_VIEW_FONTS.appName, 0);
  const appTotalWidth = APP_IMAGE_WIDTH + APP_GAP + appNameContentWidth;

  // Depot: typical depot IDs are 6-7 digits
  const depotContentWidth = measureTextWidth('1234567', RETRO_VIEW_FONTS.depot);

  // Client: IP addresses or "X clients"
  const clientSamples = actualData?.clientIps?.length
    ? actualData.clientIps
    : ['192.168.100.255', '10 clients', 'Gaming-PC'];
  const clientContentWidth = measureMaxTextWidth(clientSamples, RETRO_VIEW_FONTS.client, 0);

  // Speed: typical format "999.9 Mb/s" (bits, not bytes)
  const speedContentWidth = measureTextWidth('999.9 Mb/s', RETRO_VIEW_FONTS.speed);

  // Cache Hit/Miss: "999.99 GB • 99.9%"
  const cacheContentWidth = measureTextWidth('999.99 GB • 99.9%', RETRO_VIEW_FONTS.cacheValue);

  // Overall: "100.0%" with label
  const overallContentWidth = measureTextWidth('100.0%', RETRO_VIEW_FONTS.overall);

  // Datasource: reasonable width for labels like "Primary", "cache-1", etc.
  const datasourceContentWidth = measureTextWidth('Primary', RETRO_VIEW_FONTS.appName);

  // Events: compact width for 2 small badges
  const eventsContentWidth = 90; // Space for 2 compact badges

  // Calculate final widths: max(header, content) + padding + resize handle
  const calculateWidth = (headerW: number, contentW: number, extraPadding: number = 0): number => {
    return Math.max(
      MIN_COLUMN_WIDTH,
      Math.ceil(Math.max(headerW, contentW) + CELL_PADDING + RESIZE_HANDLE_WIDTH + extraPadding)
    );
  };

  return {
    timestamp: calculateWidth(headerWidths.timestamp, timestampContentWidth),
    banner: 140, // Fixed width for game banner images (120px image + padding)
    app: calculateWidth(headerWidths.app, appNameContentWidth, 8), // App name only (no image)
    datasource: calculateWidth(headerWidths.datasource, datasourceContentWidth),
    events: calculateWidth(headerWidths.events, eventsContentWidth),
    depot: calculateWidth(headerWidths.depot, depotContentWidth),
    client: calculateWidth(headerWidths.client, clientContentWidth),
    speed: calculateWidth(headerWidths.speed, speedContentWidth),
    cacheHit: calculateWidth(headerWidths.cacheHit, cacheContentWidth),
    cacheMiss: calculateWidth(headerWidths.cacheMiss, cacheContentWidth),
    overall: Math.max(90, calculateWidth(headerWidths.overall, overallContentWidth + 24)), // Extra for label + reset button
  };
}

/**
 * Get default column widths using sample data patterns
 * Call this once on component mount
 */
export function getDefaultColumnWidths(): ColumnWidths {
  return calculateColumnWidths();
}
