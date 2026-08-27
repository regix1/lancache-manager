/**
 * Response shapes for the REST endpoints the notification recovery configs poll.
 *
 * Each interface mirrors the C# controller response shape. Nullability follows the backend C#
 * types. The simple-recovery `createNotification` readers in the registry access these REST
 * property names directly (snake_case/camelCase as the wire delivers them) and must NOT be
 * normalized against the SignalR event shapes.
 */

import type { StageContext } from './types';
import type { OperationStatus } from '@/types/operations';
import type { CorruptionDetectionMethod } from '@/types';
import type {
  StructuralBaselineStatus,
  StructuralEffectiveScanMode,
  StructuralScanMode,
  StructuralScanSummary
} from '@/types/corruptionScan';

/** GET /api/logs/process/status - RustLogProcessorService.GetStatus() */
export interface LogProcessingStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  percentComplete: number;
  mbProcessed: number;
  mbTotal: number;
  entriesProcessed: number;
  /** Final line count; 0 while running (the Rust line-count pre-pass was removed). */
  totalLines: number;
  stageKey?: string;
  context?: StageContext;
  /** camelCase — backend anonymous object → JsonNamingPolicy.CamelCase */
  operationId?: string;
}

/** GET /api/cache/operations - ActiveOperationsResponse */
interface CacheOperationProgressItem {
  operationId?: string;
  id?: string;
  statusMessage?: string;
  stageKey?: string;
  context?: StageContext;
  percentComplete: number;
  filesDeleted: number;
  directoriesProcessed: number;
  bytesDeleted: number;
}

export interface CacheOperationsResponse {
  isProcessing: boolean;
  operations?: CacheOperationProgressItem[];
}

/** GET /api/database/reset-status - DatabaseResetStatusResponse */
export interface DatabaseResetStatusResponse {
  isProcessing: boolean;
  /** Canonical OperationStatus or null (null replaces the legacy `"idle"` sentinel). */
  status?: OperationStatus | null;
  message?: string | null;
  /** C# `double?` - genuinely nullable */
  percentComplete?: number | null;
  stageKey?: string;
  context?: StageContext;
  operationId?: string | null;
  tablesCleared?: number | null;
  totalTables?: number | null;
  filesDeleted?: number | null;
}

/** GET /api/logs/remove/status - RustServiceRemovalService.GetLogRemovalStatus() */
export interface LogRemovalStatusResponse {
  isProcessing: boolean;
  service?: string | null;
  datasource?: string | null;
  operationId?: string | null;
  filesProcessed: number;
  percentComplete?: number | null;
  linesProcessed: number;
  linesRemoved: number;
  status?: OperationStatus | null;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/system/schedules/scheduledPrefill/run-status - ScheduledPrefillRunStatusDto */
export interface ScheduledPrefillRunStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  showNotification?: boolean;
}

/** GET /api/games/detect/active - ActiveDetectionResponse */
interface GameDetectionOperationInfo {
  operationId?: string;
  statusMessage: string;
  percentComplete: number;
  scanType?: 'full' | 'incremental';
  totalGamesDetected?: number;
  context?: StageContext;
}

export interface GameDetectionStatusResponse {
  isProcessing: boolean;
  operation: GameDetectionOperationInfo | null;
  /**
   * Run-stable display flag for the active detection. A silent automatic run reports false so
   * recovery can skip resurrecting a card instead of leaving it stuck once the silent terminal arrives.
   */
  showNotification?: boolean;
}

/**
 * GET /api/cache/corruption/detect/status - CacheController.GetCorruptionDetectionStatus()
 * Returns anonymous `{ isRunning: false }` when idle, or the full object below when active.
 * Active responses carry method-aware stage/context/progress; idle responses contain only
 * `isRunning: false`. The recovery handler keeps `?? 0` for that idle/legacy boundary.
 */
export interface CorruptionDetectionStatusResponse {
  isRunning: boolean;
  operationId?: string | null;
  detectionMethod?: CorruptionDetectionMethod;
  scanMode?: StructuralScanMode;
  effectiveScanMode?: StructuralEffectiveScanMode;
  baselineStatus?: StructuralBaselineStatus;
  resumed?: boolean;
  status?: OperationStatus | null;
  message?: string | null;
  startTime?: string | null;
  stageKey?: string;
  context?: StageContext;
  percentComplete?: number;
  scanSummary?: StructuralScanSummary;
}

/** GET /api/migration/import/status - DataImportStatusResponse */
export interface DataImportStatusResponse {
  isProcessing: boolean;
  status?: OperationStatus | null;
  message?: string | null;
  /** C# `double?` - genuinely nullable */
  percentComplete?: number | null;
  operationId?: string | null;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/stats/eviction/scan/status - anonymous object from StatsController */
export interface EvictionScanStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  status: string;
  percentComplete: number;
  message: string;
  operationId: string | null;
  stageKey?: string;
  context?: StageContext;
}

/**
 * GET /api/cache/unmapped/scan/status and GET /api/cache/unmapped/removal/status - one C#
 * UnmappedCacheStatusResponse serves both. An idle body is exactly
 * `{ isProcessing: false, percentComplete: 0 }`: the API drops nulls globally
 * (Program.cs DefaultIgnoreCondition), so the other three are absent rather than null.
 */
export interface UnmappedCacheStatusResponse {
  isProcessing: boolean;
  percentComplete: number;
  operationId?: string;
  stageKey?: string;
  context?: StageContext;
}

export interface CacheSizeScanStatusResponse {
  isProcessing: boolean;
  /**
   * Run-stable display flag for the active scan. A silent automatic run reports false so
   * recovery can skip resurrecting a card instead of leaving it stuck once the silent terminal arrives.
   */
  showNotification?: boolean;
  status: string;
  percentComplete: number;
  message: string;
  operationId: string | null;
  stageKey?: string;
  context?: StageContext;
}
