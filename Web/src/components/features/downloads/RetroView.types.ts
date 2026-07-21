import type { DepotGroupedData } from './retroGrouping';
import type { EventSummary } from '../../../types';
import type { ColumnWidths } from '@utils/textMeasurement';

// Server-side hit/miss bucket filter shared with the Downloads toolbar's
// All/Hit/Miss control. Mirrors the local, unexported type of the same name
// in DownloadsTab.tsx (a .tsx file cannot export a type, so this is the
// canonical, importable declaration).
export type HitMissFilter = 'all' | 'hit' | 'miss';

// Ref handle exposed by RetroView to parent components.
export interface RetroViewHandle {
  resetWidths: () => void;
  setPageFading: (fading: boolean) => void;
}

/**
 * Live state of a divider drag. Kept in a ref, not React state, so each
 * pointermove can rewrite the grid template variable without re-rendering
 * the rows; the widths commit to state on pointerup.
 */
export interface RetroColumnDragState {
  column: keyof ColumnWidths;
  pointerId: number;
  startClientX: number;
  startWidth: number;
  /** Widths as of the latest pointermove. */
  widths: ColumnWidths;
  /**
   * True once the drag has changed a width. A plain click on a divider must
   * not commit anything, or it would flip the table into manual mode with no
   * visible change.
   */
  moved: boolean;
}

// A depot/game group plus the per-row values precomputed by RetroView so
// RetroRow renders without recalculating anything.
export interface RetroRowData extends DepotGroupedData {
  events: EventSummary[];
  hitPercent: number;
  /** Stacked timestamp display lines: ["start", "→ end" | null]. */
  timeLines: [string, string | null];
  /** Full single-line range for tooltips. */
  timeRangeTitle: string;
  hasGameImage: boolean;
  /** Canonical name-keyed service ("blizzard" | "riot") when this row's banner is name-keyed. */
  nameKeyedService: string | null;
  /** Normalized GameName slug paired with nameKeyedService. */
  nameKeyedSlug: string | null;
  onDiskSizeBytes: number | null;
}
