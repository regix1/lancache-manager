import type { DepotGroupedData } from './retroGrouping';
import type { EventSummary } from '../../../types';

// Ref handle exposed by RetroView to parent components.
export interface RetroViewHandle {
  resetWidths: () => void;
  setPageFading: (fading: boolean) => void;
}

// A depot/game group plus the per-row values precomputed by RetroView so
// RetroRow renders without recalculating anything.
export interface RetroRowData extends DepotGroupedData {
  events: EventSummary[];
  hitPercent: number;
  timeRange: string;
  accentColor: string;
  hasGameImage: boolean;
  /** Canonical name-keyed service ("blizzard" | "riot") when this row's banner is name-keyed. */
  nameKeyedService: string | null;
  /** Normalized GameName slug paired with nameKeyedService. */
  nameKeyedSlug: string | null;
  onDiskSizeBytes: number | null;
}
