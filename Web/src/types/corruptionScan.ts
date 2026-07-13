/** Structural corruption inspection strategy. Repeated-MISS requests never carry this field. */
export type StructuralScanMode = 'full' | 'incremental';

export type StructuralEffectiveScanMode = StructuralScanMode | 'baseline';

export type StructuralBaselineStatus = 'stateless' | 'building' | 'ready' | 'incomplete';

/** Durable structural scan state returned for active recovery and terminal continuity. */
export interface StructuralScanSummary {
  scanMode: StructuralScanMode;
  effectiveScanMode: StructuralEffectiveScanMode;
  baselineStatus: StructuralBaselineStatus;
  resumed: boolean;
  filesDiscovered: number;
  filesProcessed: number;
  filesReused: number;
  filesInspected: number;
  filesRevalidated: number;
  invalidFiles: number;
  filesPendingRetry: number;
  filesPruned: number;
  stateEntries: number;
  stateCommitted: boolean;
}
