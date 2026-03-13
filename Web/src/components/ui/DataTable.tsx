import React, {
  useState,
  useRef,
  useCallback,
  useEffect,
  useMemo,
  forwardRef,
  useImperativeHandle
} from 'react';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';

// --- Constants ---

const RESIZE_MIN_WIDTH_DEFAULT = 60;
const GRID_PADDING = 32; // pl-4 + pr-4 = 16px each side
const COLUMN_FIT_FLOOR = 40;

// --- Types ---

export interface DataTableColumn<T> {
  key: string;
  header: string;
  /** CSS grid value for static mode (e.g. '1fr', 'minmax(100px, 2fr)') */
  width?: string;
  /** Default pixel width when resizable (default: 150) */
  defaultWidth?: number;
  /** Minimum pixel width during drag resize (default: 60) */
  minWidth?: number;
  align?: 'left' | 'center' | 'right';
  headerClassName?: string;
  cellClassName?: string;
  render: (item: T) => React.ReactNode;
}

interface DataTableProps<T> {
  columns: DataTableColumn<T>[];
  data: T[];
  keyExtractor: (item: T) => string;
  emptyMessage?: string;
  emptyState?: React.ReactNode;
  maxHeight?: string;
  accentColor?: (item: T) => string | undefined;
  className?: string;
  stickyHeader?: boolean;
  striped?: boolean;
  compact?: boolean;
  onRowClick?: (item: T) => void;
  /** Enable column drag-resizing with handles */
  resizable?: boolean;
  /** localStorage key for persisting column widths (requires resizable) */
  storageKey?: string;
}

interface DataTableHandle {
  /** Reset all column widths to fit container without horizontal scrolling */
  resetWidths: () => void;
}

// --- Resize Handle (matches RetroView design) ---

const ResizeHandle: React.FC<{
  onMouseDown: (e: React.MouseEvent) => void;
  onDoubleClick: (e: React.MouseEvent) => void;
}> = ({ onMouseDown, onDoubleClick }) => (
  <div className="data-table-resize-handle" onMouseDown={onMouseDown} onDoubleClick={onDoubleClick}>
    {/* Subtle divider - always visible */}
    <div className="data-table-resize-line" />
    {/* Brighter line on hover */}
    <div className="data-table-resize-line-hover" />
  </div>
);

// --- Helpers ---

function getAlignClass(align: 'left' | 'center' | 'right' | undefined, prefix: string): string {
  if (align === 'center') return `${prefix}-center`;
  if (align === 'right') return `${prefix}-right`;
  return '';
}

function buildStaticGridTemplate<T>(columns: DataTableColumn<T>[]): string {
  return columns
    .map((col: DataTableColumn<T>) => {
      if (col.width) return col.width;
      if (col.minWidth) return `minmax(${col.minWidth}px, 1fr)`;
      return '1fr';
    })
    .join(' ');
}

function buildResizableGridTemplate<T>(
  columns: DataTableColumn<T>[],
  widths: Record<string, number>
): string {
  return columns
    .map((col: DataTableColumn<T>, index: number) => {
      const w = widths[col.key] || col.defaultWidth || 150;
      // Last column uses minmax to fill remaining space (like RetroView)
      if (index === columns.length - 1) {
        return `minmax(${w}px, 1fr)`;
      }
      return `${w}px`;
    })
    .join(' ');
}

function initColumnWidths<T>(
  columns: DataTableColumn<T>[],
  storageKey?: string
): Record<string, number> {
  if (storageKey) {
    try {
      const saved = localStorage.getItem(storageKey);
      if (saved) {
        const parsed = JSON.parse(saved) as Record<string, number>;
        const merged: Record<string, number> = {};
        for (const col of columns) {
          merged[col.key] = parsed[col.key] ?? col.defaultWidth ?? 150;
        }
        return merged;
      }
    } catch {
      // Ignore localStorage errors
    }
  }

  const widths: Record<string, number> = {};
  for (const col of columns) {
    widths[col.key] = col.defaultWidth ?? 150;
  }
  return widths;
}

/**
 * Fit column widths to container so there's no horizontal scrolling.
 * Uses the same proportional distribution algorithm as RetroView.
 */
function fitToContainer<T>(
  columns: DataTableColumn<T>[],
  containerWidth: number
): Record<string, number> {
  const availableWidth = containerWidth - GRID_PADDING;
  if (availableWidth <= 0) {
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.key] = col.defaultWidth ?? 150;
    }
    return widths;
  }

  const mins: Record<string, number> = {};
  const defaults: Record<string, number> = {};
  let totalDefault = 0;
  let totalMin = 0;

  for (const col of columns) {
    const min = col.minWidth ?? RESIZE_MIN_WIDTH_DEFAULT;
    const def = col.defaultWidth ?? 150;
    mins[col.key] = min;
    defaults[col.key] = def;
    totalDefault += def;
    totalMin += min;
  }

  // If all minimums exceed available width, scale down proportionally
  if (totalMin >= availableWidth) {
    const scale = availableWidth / totalMin;
    const widths: Record<string, number> = {};
    for (const col of columns) {
      widths[col.key] = Math.max(COLUMN_FIT_FLOOR, Math.floor(mins[col.key] * scale));
    }
    return widths;
  }

  // Distribute: start at minimums, then distribute extra proportionally
  const extra = availableWidth - totalMin;
  const flexTotal = columns.reduce(
    (sum: number, col: DataTableColumn<T>) => sum + Math.max(0, defaults[col.key] - mins[col.key]),
    0
  );

  const widths: Record<string, number> = {};
  for (const col of columns) {
    const flex = Math.max(0, defaults[col.key] - mins[col.key]);
    const share = flexTotal > 0 ? (flex / flexTotal) * extra : extra / columns.length;
    widths[col.key] = Math.floor(mins[col.key] + share);
  }

  return widths;
}

// --- Component ---

function DataTableInner<T>(
  {
    columns,
    data,
    keyExtractor,
    emptyMessage = 'No data available',
    emptyState,
    maxHeight,
    accentColor,
    className = '',
    stickyHeader = true,
    striped = false,
    compact = false,
    onRowClick,
    resizable = false,
    storageKey
  }: DataTableProps<T>,
  ref: React.ForwardedRef<DataTableHandle>
) {
  const containerRef = useRef<HTMLDivElement>(null);

  // --- Column width state (resizable mode only) ---
  const [columnWidths, setColumnWidths] = useState<Record<string, number>>(() =>
    resizable ? initColumnWidths(columns, storageKey) : {}
  );

  // Persist widths to localStorage when they change
  useEffect(() => {
    if (resizable && storageKey) {
      try {
        localStorage.setItem(storageKey, JSON.stringify(columnWidths));
      } catch {
        // Ignore localStorage errors
      }
    }
  }, [resizable, storageKey, columnWidths]);

  // --- Grid template ---
  const gridTemplate = useMemo(() => {
    if (resizable) {
      return buildResizableGridTemplate(columns, columnWidths);
    }
    return buildStaticGridTemplate(columns);
  }, [columns, columnWidths, resizable]);

  // --- Drag handling (same technique as RetroView) ---
  const handleMouseDown = useCallback(
    (columnKey: string, e: React.MouseEvent) => {
      if (!resizable) return;
      e.preventDefault();
      e.stopPropagation();

      const startX = e.clientX;
      const startWidth = columnWidths[columnKey] || 150;
      const col = columns.find((c: DataTableColumn<T>) => c.key === columnKey);
      const minWidth = col?.minWidth ?? RESIZE_MIN_WIDTH_DEFAULT;

      const handleMouseMove = (moveEvent: MouseEvent) => {
        const diff = moveEvent.clientX - startX;
        const newWidth = Math.max(minWidth, startWidth + diff);
        setColumnWidths((prev: Record<string, number>) => ({ ...prev, [columnKey]: newWidth }));
      };

      const handleMouseUp = () => {
        document.removeEventListener('mousemove', handleMouseMove);
        document.removeEventListener('mouseup', handleMouseUp);
        document.body.style.cursor = '';
        document.body.style.userSelect = '';
      };

      document.addEventListener('mousemove', handleMouseMove);
      document.addEventListener('mouseup', handleMouseUp);
      document.body.style.cursor = 'col-resize';
      document.body.style.userSelect = 'none';
    },
    [resizable, columnWidths, columns]
  );

  // Double-click resize handle → reset that column to its defaultWidth
  const handleDoubleClick = useCallback(
    (columnKey: string) => {
      if (!resizable) return;
      const col = columns.find((c: DataTableColumn<T>) => c.key === columnKey);
      if (col) {
        setColumnWidths((prev: Record<string, number>) => ({
          ...prev,
          [columnKey]: col.defaultWidth ?? 150
        }));
      }
    },
    [resizable, columns]
  );

  // --- Reset widths: fit all columns to container (no horizontal scrolling) ---
  const handleResetWidths = useCallback(() => {
    if (!resizable) return;

    if (storageKey) {
      try {
        localStorage.removeItem(storageKey);
      } catch {
        // Ignore
      }
    }

    const containerWidth = containerRef.current?.clientWidth;
    if (containerWidth) {
      setColumnWidths(fitToContainer(columns, containerWidth));
    } else {
      // Fallback to defaults
      const widths: Record<string, number> = {};
      for (const col of columns) {
        widths[col.key] = col.defaultWidth ?? 150;
      }
      setColumnWidths(widths);
    }
  }, [resizable, storageKey, columns]);

  // Expose resetWidths via ref (like RetroView's RetroViewHandle)
  useImperativeHandle(ref, () => ({ resetWidths: handleResetWidths }), [handleResetWidths]);

  // --- CSS classes ---
  const containerClasses = [
    'data-table',
    striped ? 'data-table-striped' : '',
    compact ? 'data-table-compact' : '',
    resizable ? 'data-table-resizable' : '',
    className
  ]
    .filter(Boolean)
    .join(' ');

  const headerClasses = ['data-table-header', !stickyHeader ? 'data-table-header-non-sticky' : '']
    .filter(Boolean)
    .join(' ');

  // --- Render header ---
  const renderHeader = () => (
    <div className={headerClasses} style={{ gridTemplateColumns: gridTemplate }} role="row">
      {columns.map((col: DataTableColumn<T>, index: number) => {
        const isLastColumn = index === columns.length - 1;
        const alignClass = getAlignClass(col.align, 'data-table-header-cell');
        const cellClasses = [
          'data-table-header-cell',
          resizable && !isLastColumn ? 'data-table-header-cell-resizable' : '',
          alignClass,
          col.headerClassName || ''
        ]
          .filter(Boolean)
          .join(' ');

        return (
          <div key={col.key} className={cellClasses} role="columnheader">
            <span className="data-table-header-label">{col.header}</span>
            {resizable && !isLastColumn && (
              <ResizeHandle
                onMouseDown={(e: React.MouseEvent) => handleMouseDown(col.key, e)}
                onDoubleClick={() => handleDoubleClick(col.key)}
              />
            )}
          </div>
        );
      })}
    </div>
  );

  // --- Render row ---
  const renderRow = (item: T) => {
    const key = keyExtractor(item);
    const accent = accentColor ? accentColor(item) : undefined;

    const rowClasses = ['data-table-row', onRowClick ? 'data-table-row-clickable' : '']
      .filter(Boolean)
      .join(' ');

    const handleRowClick = onRowClick ? () => onRowClick(item) : undefined;
    const handleRowKeyDown = onRowClick
      ? (e: React.KeyboardEvent<HTMLDivElement>) => {
          if (e.key === 'Enter' || e.key === ' ') {
            e.preventDefault();
            onRowClick(item);
          }
        }
      : undefined;

    return (
      <div
        key={key}
        className={rowClasses}
        onClick={handleRowClick}
        onKeyDown={handleRowKeyDown}
        role="row"
        tabIndex={onRowClick ? 0 : undefined}
      >
        {accent && <div className="data-table-row-accent" style={{ backgroundColor: accent }} />}
        <div className="data-table-row-grid" style={{ gridTemplateColumns: gridTemplate }}>
          {columns.map((col: DataTableColumn<T>) => {
            const alignClass = getAlignClass(col.align, 'data-table-cell');
            const cellClasses = ['data-table-cell', alignClass, col.cellClassName || '']
              .filter(Boolean)
              .join(' ');

            return (
              <div key={col.key} className={cellClasses} role="cell">
                {col.render(item)}
              </div>
            );
          })}
        </div>
      </div>
    );
  };

  // --- Render body ---
  const renderBody = () => {
    if (data.length === 0) {
      if (emptyState) return emptyState;
      return (
        <div className="data-table-empty" role="row">
          <span role="cell">{emptyMessage}</span>
        </div>
      );
    }

    return (
      <div className="data-table-body" role="rowgroup">
        {data.map((item: T) => renderRow(item))}
      </div>
    );
  };

  // When maxHeight is specified, only the body scrolls — header stays fixed above
  if (maxHeight) {
    return (
      <div ref={containerRef} className={containerClasses} role="table">
        {renderHeader()}
        <CustomScrollbar maxHeight={maxHeight} paddingMode="compact">
          <div className="data-table-scroll-wrapper">{renderBody()}</div>
        </CustomScrollbar>
      </div>
    );
  }

  return (
    <div ref={containerRef} className={containerClasses} role="table">
      <div className="data-table-scroll-wrapper">
        {renderHeader()}
        {renderBody()}
      </div>
    </div>
  );
}

// forwardRef wrapper that preserves generic type parameter
export const DataTable = forwardRef(DataTableInner) as <T>(
  props: DataTableProps<T> & { ref?: React.Ref<DataTableHandle> }
) => React.ReactElement | null;
