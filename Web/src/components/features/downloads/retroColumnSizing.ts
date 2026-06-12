import { measureTextWidth, RETRO_VIEW_FONTS, type ColumnWidths } from '@utils/textMeasurement';

export const RETRO_WIDTHS_STORAGE_KEY = 'retro-view-column-widths';

const GRID_GAP = 8;
const GRID_PADDING = 32;
const GRID_FIXED_ADDITIONS = 20; // overall +20 in grid template
export const RESIZE_MIN_WIDTH = 60;
const COLUMN_FIT_FLOOR = 40;

const MIN_COLUMN_WIDTHS: ColumnWidths = {
  timestamp: 80,
  banner: 130,
  app: 100,
  datasource: 70,
  events: 50,
  depot: 40,
  client: 60,
  speed: 50,
  cacheHit: 80,
  cacheMiss: 0,
  overall: 50
};

// Baseline widths used as the floor when measuring real content.
const MEASURE_BASE_WIDTHS: ColumnWidths = {
  timestamp: 80,
  banner: 140,
  app: 100,
  datasource: 75,
  events: 90,
  depot: 50,
  client: 70,
  speed: 60,
  cacheHit: 150,
  cacheMiss: 0,
  overall: 80
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

const getAvailableGridWidth = (
  containerWidth: number,
  visibility: RetroColumnVisibility
): number => {
  const columns = getVisibleColumns(visibility);
  const columnCount = columns.length;
  const gapCount = columnCount - 1;
  return containerWidth - GRID_PADDING - gapCount * GRID_GAP - GRID_FIXED_ADDITIONS;
};

export const fitWidthsToContainer = (
  widths: ColumnWidths,
  containerWidth: number,
  visibility: RetroColumnVisibility,
  lockedMinWidths?: Partial<ColumnWidths>
): ColumnWidths => {
  const availableWidth = getAvailableGridWidth(containerWidth, visibility);
  if (!Number.isFinite(availableWidth) || availableWidth <= 0) {
    return widths;
  }

  const columns = getVisibleColumns(visibility);
  const minWidths: ColumnWidths = { ...MIN_COLUMN_WIDTHS };
  if (lockedMinWidths) {
    Object.entries(lockedMinWidths).forEach(([column, value]) => {
      const key = column as keyof ColumnWidths;
      minWidths[key] = Math.max(minWidths[key], value ?? 0);
    });
  }

  const normalized: ColumnWidths = { ...widths, cacheMiss: 0 };
  columns.forEach((column) => {
    normalized[column] = Math.max(minWidths[column], normalized[column]);
  });

  const totalWidth = columns.reduce((sum, column) => sum + normalized[column], 0);
  if (totalWidth <= availableWidth) {
    return normalized;
  }

  const totalMin = columns.reduce((sum, column) => sum + minWidths[column], 0);
  if (totalMin >= availableWidth) {
    const scale = availableWidth / totalMin;
    const scaled: ColumnWidths = { ...normalized, cacheMiss: 0 };
    columns.forEach((column) => {
      scaled[column] = Math.max(COLUMN_FIT_FLOOR, Math.floor(minWidths[column] * scale));
    });
    return scaled;
  }

  const extra = availableWidth - totalMin;
  const flexTotal = columns.reduce(
    (sum, column) => sum + Math.max(0, normalized[column] - minWidths[column]),
    0
  );
  const fitted: ColumnWidths = { ...normalized, cacheMiss: 0 };
  columns.forEach((column) => {
    const flex = Math.max(0, normalized[column] - minWidths[column]);
    const share = flexTotal > 0 ? (flex / flexTotal) * extra : 0;
    fitted[column] = Math.floor(minWidths[column] + share);
  });

  return fitted;
};

// Build the grid-template-columns value applied as the --retro-grid-cols CSS
// variable on the table container. Rows and the header consume the variable,
// so resize drags only touch one DOM node instead of re-rendering every row.
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
    `${widths.cacheHit + widths.cacheMiss}px`,
    `minmax(${widths.overall + 20}px, 1fr)`
  );
  return parts.join(' ');
};

// Pre-formatted strings for one row, used for canvas text measurement.
export interface RetroMeasureRow {
  timeRangeFull: string;
  appName: string;
  serviceBadge: string;
  evictionLabel: string;
  datasourceLabel: string;
  depotLabel: string;
  clientLabel: string;
  speedLabel: string;
  hitLabel: string;
  missLabel: string;
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
  let width = MEASURE_BASE_WIDTHS[column];

  switch (column) {
    case 'timestamp':
      width = Math.max(
        width,
        maxRowWidth(rows, RETRO_VIEW_FONTS.timestamp, (r) => r.timeRangeFull, 12)
      );
      break;
    case 'banner':
      // Fixed size for game artwork (120px image + padding)
      width = 140;
      break;
    case 'app':
      rows.forEach((row) => {
        const gameNameWidth = measureTextWidth(row.appName, RETRO_VIEW_FONTS.appName) + 32;
        // Account for BadgesRow width: service badge + optional eviction badge + padding/gaps
        const serviceBadgeWidth = measureTextWidth(row.serviceBadge, RETRO_VIEW_FONTS.badge) + 24;
        const evictionBadgeWidth = row.evictionLabel
          ? measureTextWidth(row.evictionLabel, RETRO_VIEW_FONTS.badge) + 24 + 6
          : 0;
        const badgesWidth = serviceBadgeWidth + evictionBadgeWidth + 32;
        width = Math.max(width, gameNameWidth, badgesWidth);
      });
      break;
    case 'datasource':
      width = Math.max(
        width,
        maxRowWidth(rows, RETRO_VIEW_FONTS.datasource, (r) => r.datasourceLabel, 32)
      );
      break;
    case 'depot':
      width = Math.max(
        width,
        maxRowWidth(rows, RETRO_VIEW_FONTS.depot, (r) => r.depotLabel, 32)
      );
      break;
    case 'client':
      width = Math.max(
        width,
        maxRowWidth(rows, RETRO_VIEW_FONTS.client, (r) => r.clientLabel, 32)
      );
      break;
    case 'speed':
      // +16 extra for the inline speed icon
      width = Math.max(
        width,
        maxRowWidth(rows, RETRO_VIEW_FONTS.speed, (r) => r.speedLabel, 32 + 16)
      );
      break;
    case 'cacheHit':
      rows.forEach((row) => {
        const hitWidth = measureTextWidth(row.hitLabel, RETRO_VIEW_FONTS.cacheValue);
        const missWidth = measureTextWidth(row.missLabel, RETRO_VIEW_FONTS.cacheValue);
        width = Math.max(width, hitWidth + missWidth + 8 + 32);
      });
      break;
    default:
      // events / overall are header-driven
      break;
  }

  const headerPadding = column === 'cacheHit' ? 16 : 32;
  width = Math.max(width, measureTextWidth(headerLabel, RETRO_VIEW_FONTS.header) + headerPadding);

  return Math.max(RESIZE_MIN_WIDTH, Math.ceil(width));
};

/**
 * Measure every visible column from real row content, then fit the result to
 * the container width. Used by the auto-fit-on-load and "fit columns" actions.
 */
export const measureAllRetroColumns = (
  rows: RetroMeasureRow[],
  headers: Record<keyof ColumnWidths, string>,
  visibility: RetroColumnVisibility,
  containerWidth: number
): ColumnWidths => {
  const measured: ColumnWidths = { ...MEASURE_BASE_WIDTHS };
  getVisibleColumns(visibility).forEach((column) => {
    measured[column] = measureRetroColumn(column, rows, headers[column]);
  });
  return fitWidthsToContainer(measured, containerWidth, visibility);
};
