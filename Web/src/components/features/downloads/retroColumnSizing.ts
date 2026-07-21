import { measureTextWidth, getRetroViewFonts, type ColumnWidths } from '@utils/textMeasurement';

export const RETRO_WIDTHS_STORAGE_KEY = 'retro-view-column-widths';

// Horizontal row padding (1rem each side) that the grid template cannot use.
const GRID_PADDING = 32;

// Hard floor for a user-sized column; content below this truncates.
export const RESIZE_MIN_WIDTH = 48;

// Rendered size of the efficiency dial (mirrors .retro-gauge-dial). The row
// renderer draws its SVG at this size and the auto-fit measurement uses it as
// the gauge column's content width.
export const GAUGE_DIAL_SIZE = 44;

// Fallback widths for columns whose content cannot be canvas-measured
// (event badges, artwork) and the initial render widths before the first
// auto-fit pass runs.
const MEASURE_BASE_WIDTHS: ColumnWidths = {
  timestamp: 120,
  banner: 120,
  app: 100,
  datasource: 75,
  events: 90,
  depot: 50,
  client: 70,
  speed: 60,
  cacheHit: 150,
  overall: 64
};

export const getDefaultColumnWidths = (): ColumnWidths => ({ ...MEASURE_BASE_WIDTHS });

const COLUMN_KEYS = Object.keys(MEASURE_BASE_WIDTHS) as (keyof ColumnWidths)[];

/**
 * Load persisted manual widths. Unknown keys are dropped and every value must
 * be a finite number, so a stale or hand-edited entry can never produce a
 * broken grid template; missing columns fall back to their defaults.
 */
export const readStoredWidths = (): ColumnWidths | null => {
  try {
    const saved = localStorage.getItem(RETRO_WIDTHS_STORAGE_KEY);
    if (!saved) return null;
    const parsed: unknown = JSON.parse(saved);
    if (typeof parsed !== 'object' || parsed === null) return null;
    const raw = parsed as Record<string, unknown>;
    const widths = getDefaultColumnWidths();
    COLUMN_KEYS.forEach((key) => {
      const value = raw[key];
      if (typeof value === 'number' && Number.isFinite(value)) {
        widths[key] = Math.max(RESIZE_MIN_WIDTH, Math.round(value));
      }
    });
    // Entries written before the cache column became a single track persisted
    // it as separate hit/miss widths that rendered merged; fold the legacy
    // miss share back in so an old entry keeps its overall cache width.
    const legacyMiss = raw.cacheMiss;
    if (typeof legacyMiss === 'number' && Number.isFinite(legacyMiss) && legacyMiss > 0) {
      widths.cacheHit += Math.round(legacyMiss);
    }
    return widths;
  } catch {
    // Ignore localStorage errors
    return null;
  }
};

export interface RetroColumnVisibility {
  showDatasource: boolean;
  showTimestamps: boolean;
  showBanner: boolean;
}

const getVisibleColumns = ({
  showDatasource,
  showTimestamps,
  showBanner
}: RetroColumnVisibility): (keyof ColumnWidths)[] => {
  const columns: (keyof ColumnWidths)[] = [];
  if (showTimestamps) columns.push('timestamp');
  if (showBanner) columns.push('banner');
  columns.push('app');
  if (showDatasource) columns.push('datasource');
  columns.push('events', 'depot', 'client', 'speed', 'cacheHit', 'overall');
  return columns;
};

// Columns excluded from auto-fit expansion: the banner box is fixed-size
// artwork and the gauge reads best at its content width (leftover space used
// to pool in the gauge column via a 1fr track, leaving the dial adrift in a
// far-too-wide last column).
const FIXED_CONTENT_COLUMNS: ReadonlySet<keyof ColumnWidths> = new Set(['banner', 'overall']);

/**
 * Auto-fit: distribute leftover container width across the text columns in
 * proportion to their measured widths. Measured widths are never reduced;
 * when content is wider than the container the table scrolls horizontally.
 */
export const fitMeasuredWidthsToContainer = (
  measured: ColumnWidths,
  containerWidth: number,
  visibility: RetroColumnVisibility
): ColumnWidths => {
  const available = containerWidth - GRID_PADDING;
  if (!Number.isFinite(available) || available <= 0) {
    return measured;
  }

  const columns = getVisibleColumns(visibility);
  const total = columns.reduce((sum, column) => sum + measured[column], 0);
  const extra = available - total;
  if (extra <= 0) {
    return measured;
  }

  const flexColumns = columns.filter((column) => !FIXED_CONTENT_COLUMNS.has(column));
  const flexTotal = flexColumns.reduce((sum, column) => sum + measured[column], 0);
  if (flexTotal <= 0) {
    return measured;
  }

  const fitted: ColumnWidths = { ...measured };
  let used = 0;
  flexColumns.forEach((column) => {
    const share = Math.floor((extra * measured[column]) / flexTotal);
    fitted[column] += share;
    used += share;
  });
  // Rounding remainder goes to the app column so the grid fills the container
  // to the pixel.
  fitted.app += extra - used;
  return fitted;
};

// Build the grid-template-columns value applied as the --retro-grid-cols CSS
// variable on the table container. Rows and the header consume the variable,
// so resize drags only touch one DOM node instead of re-rendering every row.
// Every track is an exact pixel width: resizing one column changes the
// table's total width and the container's horizontal scrollbar absorbs the
// difference, leaving every other column exactly where the user put it.
export const buildGridTemplate = (
  widths: ColumnWidths,
  visibility: RetroColumnVisibility
): string => {
  const parts: string[] = [];
  if (visibility.showTimestamps) parts.push(`${widths.timestamp}px`);
  if (visibility.showBanner) parts.push(`${widths.banner}px`);
  parts.push(`${widths.app}px`);
  if (visibility.showDatasource) parts.push(`${widths.datasource}px`);
  parts.push(
    `${widths.events}px`,
    `${widths.depot}px`,
    `${widths.client}px`,
    `${widths.speed}px`,
    `${widths.cacheHit}px`,
    `${widths.overall}px`
  );
  return parts.join(' ');
};

// Pre-formatted strings for one row, used for canvas text measurement.
export interface RetroMeasureRow {
  timeLines: [string, string | null];
  appName: string;
  serviceBadge: string;
  evictionLabel: string;
  onDiskLabel: string;
  datasourceLabel: string;
  depotLabel: string;
  clientLabel: string;
  clientSubLabel: string;
  speedLabel: string;
  hitLabel: string;
  missLabel: string;
  gaugeLabel: string;
}

const maxRowWidth = (
  rows: RetroMeasureRow[],
  font: string,
  getText: (row: RetroMeasureRow) => string,
  padding: number
): number =>
  rows.reduce((max, row) => {
    const text = getText(row);
    return text ? Math.max(max, measureTextWidth(text, font) + padding) : max;
  }, 0);

/**
 * Measure the width one column needs to show its content and header without
 * truncation. Canvas-based: no DOM nodes, no forced reflows.
 */
export const measureRetroColumn = (
  column: keyof ColumnWidths,
  rows: RetroMeasureRow[],
  headerLabel: string
): number => {
  const fonts = getRetroViewFonts();
  let width = MEASURE_BASE_WIDTHS[column];

  switch (column) {
    case 'timestamp':
      width = Math.max(
        width,
        maxRowWidth(rows, fonts.timestamp, (r) => r.timeLines[0], 16),
        maxRowWidth(rows, fonts.timestamp, (r) => r.timeLines[1] ?? '', 16)
      );
      break;
    case 'banner':
      // Fixed size for game artwork (104px image + padding)
      width = 120;
      break;
    case 'app':
      rows.forEach((row) => {
        const gameNameWidth = measureTextWidth(row.appName, fonts.appName) + 32;
        // Account for BadgesRow width: service badge + optional eviction badge + padding/gaps
        const serviceBadgeWidth = measureTextWidth(row.serviceBadge, fonts.badge) + 24;
        const evictionBadgeWidth = row.evictionLabel
          ? measureTextWidth(row.evictionLabel, fonts.badge) + 24 + 6
          : 0;
        const badgesWidth = serviceBadgeWidth + evictionBadgeWidth + 32;
        const onDiskWidth = row.onDiskLabel
          ? measureTextWidth(row.onDiskLabel, fonts.onDisk) + 32
          : 0;
        width = Math.max(width, gameNameWidth, badgesWidth, onDiskWidth);
      });
      break;
    case 'datasource':
      width = Math.max(
        width,
        maxRowWidth(rows, fonts.datasource, (r) => r.datasourceLabel, 32)
      );
      break;
    case 'depot':
      width = Math.max(
        width,
        maxRowWidth(rows, fonts.depot, (r) => r.depotLabel, 32)
      );
      break;
    case 'client':
      width = Math.max(
        width,
        maxRowWidth(rows, fonts.client, (r) => r.clientLabel, 32),
        maxRowWidth(rows, fonts.clientSub, (r) => r.clientSubLabel, 32)
      );
      break;
    case 'speed':
      width = Math.max(
        width,
        maxRowWidth(rows, fonts.speed, (r) => r.speedLabel, 32)
      );
      break;
    case 'cacheHit':
      rows.forEach((row) => {
        const hitWidth = measureTextWidth(row.hitLabel, fonts.cacheValue);
        const missWidth = measureTextWidth(row.missLabel, fonts.cacheValue);
        width = Math.max(width, hitWidth + missWidth + 8 + 32);
      });
      break;
    case 'overall':
      // Gauge column: dial plus the widest tier label under it. Padding on
      // the label absorbs its letter-spacing, which canvas cannot measure.
      width = Math.max(
        width,
        GAUGE_DIAL_SIZE + 16,
        maxRowWidth(rows, fonts.gaugeLabel, (r) => r.gaugeLabel, 24)
      );
      break;
    default:
      // events is badge-driven; its base width applies.
      break;
  }

  const headerPadding = column === 'cacheHit' ? 16 : 32;
  width = Math.max(width, measureTextWidth(headerLabel, fonts.header) + headerPadding);

  return Math.max(RESIZE_MIN_WIDTH, Math.ceil(width));
};

/**
 * Measure every visible column from real row content. Callers can then keep
 * these content widths for horizontal scrolling or pass them through
 * fitMeasuredWidthsToContainer for responsive auto-fit behavior.
 */
export const measureAllRetroColumns = (
  rows: RetroMeasureRow[],
  headers: Record<keyof ColumnWidths, string>,
  visibility: RetroColumnVisibility
): ColumnWidths => {
  const measured: ColumnWidths = { ...MEASURE_BASE_WIDTHS };
  getVisibleColumns(visibility).forEach((column) => {
    measured[column] = measureRetroColumn(column, rows, headers[column]);
  });
  return measured;
};
