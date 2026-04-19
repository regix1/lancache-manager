import type {
  NotificationType,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss
} from './types';
import type { OperationStatus } from '@/types/operations';
import { NOTIFICATION_STORAGE_KEYS, NOTIFICATION_IDS } from './constants';
import {
  formatLogProcessingRecoveryMessage,
  formatLogProcessingRecoveryDetailMessage,
  formatDepotMappingRecoveryDetailMessage
} from './detailMessageFormatters';
import i18n from '@/i18n';

export type FetchWithAuth = (url: string) => Promise<Response>;

// ============================================================================
// Per-endpoint Recovery Response DTOs
// ============================================================================
// Each interface mirrors the C# controller response shape. Nullability follows
// the backend C# DTOs (verified in phase2-2B-recovery.md). These types replace
// the previous `Record<string, unknown>` untyped access pattern.

type StageContext = Record<string, string | number | boolean>;

/** GET /api/logs/process/status — RustLogProcessorService.GetStatus() */
interface LogProcessingStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  percentComplete: number;
  mbProcessed: number;
  mbTotal: number;
  entriesProcessed: number;
  totalLines: number;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/cache/operations — ActiveOperationsResponse */
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

interface CacheOperationsResponse {
  isProcessing: boolean;
  operations?: CacheOperationProgressItem[];
}

/** GET /api/database/reset-status — DatabaseResetStatusResponse */
interface DatabaseResetStatusResponse {
  isProcessing: boolean;
  /** Canonical OperationStatus or null (null replaces the legacy `"idle"` sentinel). */
  status?: OperationStatus | null;
  message?: string | null;
  /** C# `int?` — genuinely nullable */
  percentComplete?: number | null;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/depots/rebuild/progress — SteamPicsProgress */
interface DepotRebuildProgressResponse {
  isProcessing: boolean;
  statusMessage: string;
  progressPercent: number;
  processedBatches?: number;
  totalBatches?: number;
  depotMappingsFound?: number;
  totalMappings: number;
  processedMappings: number;
  isLoggedOn: boolean;
  operationId?: string;
}

/** GET /api/logs/remove/status — RustServiceRemovalService.GetLogRemovalStatus() */
interface LogRemovalStatusResponse {
  isProcessing: boolean;
  service: string;
  percentComplete: number;
  linesProcessed: number;
  linesRemoved: number;
  stageKey?: string;
  context?: StageContext;
}

/** GET /api/games/detect/active — ActiveDetectionResponse */
interface GameDetectionOperationInfo {
  operationId?: string;
  statusMessage: string;
  percentComplete: number;
  scanType?: 'full' | 'incremental';
}

interface GameDetectionStatusResponse {
  isProcessing: boolean;
  operation: GameDetectionOperationInfo | null;
}

/**
 * GET /api/cache/corruption/detect/status — CacheController.GetCorruptionDetectionStatus()
 * Returns anonymous `{ isRunning: false }` when idle, or the full object below when active.
 * NOTE: backend does NOT emit `percentComplete` — the field is absent from the anonymous
 * response object. The recovery handler uses `?? 0` as a gap-filler. To fix properly,
 * add `percentComplete = activeOp.PercentComplete` to the anonymous object in CacheController.cs.
 */
interface CorruptionDetectionStatusResponse {
  isRunning: boolean;
  operationId?: string;
  status?: string;
  message?: string;
  startTime?: string;
  stageKey?: string;
  context?: StageContext;
  /** Not emitted by backend — always undefined on the wire. `?? 0` fallback applies. */
  percentComplete?: number;
}

/** GET /api/migration/import/status — DataImportStatusResponse */
interface DataImportStatusResponse {
  isProcessing: boolean;
  status?: string | null;
  message?: string | null;
  /** C# `double?` — genuinely nullable */
  percentComplete?: number | null;
  operationId?: string | null;
  stageKey?: string;
  context?: StageContext;
}

/**
 * GET /api/epic/game-mappings/schedule — EpicGameMappingController.GetScheduleStatus()
 * Returns EpicScheduleStatus from EpicMappingService. All fields verified against
 * Api/LancacheManager/Core/Services/EpicMapping/EpicMappingService.cs (class EpicScheduleStatus).
 */
interface EpicGameMappingScheduleResponse {
  /** Always present */
  isProcessing: boolean;
  /** C# `string?` — only set when IsProcessing is true; null/absent when idle */
  statusMessage?: string | null;
  /** C# `double` (non-null) — always emitted; 0 when not processing */
  progressPercent: number;
  /** C# `string?` — nullable */
  operationId?: string | null;
  /** Additional fields from EpicScheduleStatus (not used by recovery handler) */
  refreshIntervalHours?: number;
  nextRefreshIn?: number;
  lastRefreshTime?: string | null;
  isAuthenticated?: boolean;
  status?: string;
}

/** GET /api/stats/eviction/scan/status — anonymous object from StatsController */
interface EvictionScanStatusResponse {
  isProcessing: boolean;
  silentMode: boolean;
  status: string;
  percentComplete: number;
  message: string;
  operationId: string | null;
  stageKey?: string;
  context?: StageContext;
}

// ============================================================================
// Simple Recovery Config (for fixed-ID operations)
// ============================================================================

interface SimpleRecoveryConfig<TData> {
  apiEndpoint: string;
  storageKey: string;
  type: NotificationType;
  notificationId: string;
  isProcessing: (data: TData) => boolean;
  shouldSkip?: (data: TData) => boolean;
  createNotification: (
    data: TData
  ) => Omit<UnifiedNotification, 'id' | 'type' | 'status' | 'startedAt'>;
  staleMessage: string;
}

function createSimpleRecoveryFunction<TData>(
  config: SimpleRecoveryConfig<TData>,
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  return async () => {
    try {
      const response = await fetchWithAuth(config.apiEndpoint);
      if (!response.ok) return;

      const data = (await response.json()) as TData;
      const notificationId = config.notificationId;

      // Check if we should skip (e.g., silent mode)
      if (config.shouldSkip?.(data)) {
        localStorage.removeItem(config.storageKey);
        setNotifications((prev: UnifiedNotification[]) =>
          prev.filter((n) => n.type !== config.type)
        );
        return;
      }

      if (config.isProcessing(data)) {
        const notificationData = config.createNotification(data);
        setNotifications((prev: UnifiedNotification[]) => {
          const filtered = prev.filter((n) => n.type !== config.type);
          return [
            ...filtered,
            {
              id: notificationId,
              type: config.type,
              status: 'running' as NotificationStatus,
              startedAt: new Date(),
              ...notificationData
            }
          ];
        });
      } else {
        // Clear stale localStorage entry if present
        const saved = localStorage.getItem(config.storageKey);
        if (saved) {
          localStorage.removeItem(config.storageKey);
        }

        // Always transition any running notification of this type to completed.
        // This handles both:
        // 1. Notifications restored from localStorage (stale from previous session)
        // 2. Notifications created by a previous recovery poll (when isProcessing was true)
        //    — these don't use localStorage, so the old `if (saved)` guard missed them
        setNotifications((prev: UnifiedNotification[]) => {
          const existing = prev.find((n) => n.type === config.type && n.status === 'running');
          if (!existing) return prev;

          return prev.map((n) => {
            if (n.type === config.type && n.status === 'running') {
              return {
                ...n,
                id: notificationId,
                status: 'completed' as NotificationStatus,
                message: config.staleMessage,
                progress: 100
              };
            }
            return n;
          });
        });

        scheduleAutoDismiss(notificationId);
      }
    } catch {
      // Silently fail - operation not running
    }
  };
}

// ============================================================================
// Recovery Configurations
// ============================================================================

const RECOVERY_CONFIGS = {
  logProcessing: {
    apiEndpoint: '/api/logs/process/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING,
    type: 'log_processing' as NotificationType,
    notificationId: NOTIFICATION_IDS.LOG_PROCESSING,
    isProcessing: (data: LogProcessingStatusResponse) => data.isProcessing && !data.silentMode,
    shouldSkip: (data: LogProcessingStatusResponse) => data.isProcessing && data.silentMode,
    createNotification: (data: LogProcessingStatusResponse) => ({
      message: formatLogProcessingRecoveryMessage(data.mbProcessed, data.mbTotal),
      detailMessage: formatLogProcessingRecoveryDetailMessage(
        data.entriesProcessed,
        data.totalLines
      ),
      progress: Math.min(99.9, data.percentComplete),
      details: {
        mbProcessed: data.mbProcessed,
        mbTotal: data.mbTotal,
        entriesProcessed: data.entriesProcessed,
        totalLines: data.totalLines
      }
    }),
    staleMessage: 'Log processing completed'
  } satisfies SimpleRecoveryConfig<LogProcessingStatusResponse>,

  cacheClearing: {
    apiEndpoint: '/api/cache/operations',
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
    type: 'cache_clearing' as NotificationType,
    notificationId: NOTIFICATION_IDS.CACHE_CLEARING,
    isProcessing: (data: CacheOperationsResponse) =>
      data.isProcessing && Boolean(data.operations?.length),
    createNotification: (data: CacheOperationsResponse) => {
      const activeOp = data.operations?.[0];
      return {
        message:
          activeOp?.statusMessage ??
          (activeOp?.stageKey ? i18n.t(activeOp.stageKey, activeOp.context ?? {}) : undefined) ??
          i18n.t('signalr.cacheClear.starting'),
        progress: activeOp?.percentComplete ?? 0,
        details: {
          operationId: activeOp?.operationId ?? activeOp?.id,
          filesDeleted: activeOp?.filesDeleted ?? 0,
          directoriesProcessed: activeOp?.directoriesProcessed ?? 0,
          bytesDeleted: activeOp?.bytesDeleted ?? 0
        }
      };
    },
    staleMessage: 'Cache clearing completed'
  } satisfies SimpleRecoveryConfig<CacheOperationsResponse>,

  databaseReset: {
    apiEndpoint: '/api/database/reset-status',
    storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
    type: 'database_reset' as NotificationType,
    notificationId: NOTIFICATION_IDS.DATABASE_RESET,
    isProcessing: (data: DatabaseResetStatusResponse) => data.isProcessing,
    createNotification: (data: DatabaseResetStatusResponse) => ({
      message: data.stageKey
        ? i18n.t(data.stageKey, data.context ?? {})
        : i18n.t('signalr.dbReset.starting'),
      // `??` (not `||`): backend field is `int?` — nullable. `??` preserves 0.
      progress: data.percentComplete ?? 0
    }),
    staleMessage: 'Database reset completed'
  } satisfies SimpleRecoveryConfig<DatabaseResetStatusResponse>,

  depotMapping: {
    apiEndpoint: '/api/depots/rebuild/progress',
    storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
    type: 'depot_mapping' as NotificationType,
    notificationId: NOTIFICATION_IDS.DEPOT_MAPPING,
    isProcessing: (data: DepotRebuildProgressResponse) => data.isProcessing,
    createNotification: (data: DepotRebuildProgressResponse) => {
      const detailMessage = formatDepotMappingRecoveryDetailMessage({
        processedBatches: data.processedBatches,
        totalBatches: data.totalBatches,
        depotMappingsFound: data.depotMappingsFound
      });

      return {
        message: data.statusMessage,
        detailMessage,
        progress: data.progressPercent,
        details: {
          operationId: data.operationId,
          totalMappings: data.totalMappings,
          processedMappings: data.processedMappings,
          isLoggedOn: data.isLoggedOn,
          percentComplete: data.progressPercent
        }
      };
    },
    staleMessage: 'Depot mapping completed'
  } satisfies SimpleRecoveryConfig<DepotRebuildProgressResponse>,

  logRemoval: {
    apiEndpoint: '/api/logs/remove/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
    type: 'log_removal' as NotificationType,
    notificationId: NOTIFICATION_IDS.LOG_REMOVAL,
    isProcessing: (data: LogRemovalStatusResponse) => data.isProcessing && Boolean(data.service),
    createNotification: (data: LogRemovalStatusResponse) => ({
      message: data.stageKey
        ? i18n.t(data.stageKey, data.context ?? {})
        : i18n.t('signalr.logRemoval.starting.default', { service: data.service }),
      progress: data.percentComplete,
      details: {
        service: data.service,
        linesProcessed: data.linesProcessed,
        linesRemoved: data.linesRemoved
      }
    }),
    staleMessage: 'Log entry removal completed'
  } satisfies SimpleRecoveryConfig<LogRemovalStatusResponse>,

  gameDetection: {
    apiEndpoint: '/api/games/detect/active',
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
    type: 'game_detection' as NotificationType,
    notificationId: NOTIFICATION_IDS.GAME_DETECTION,
    isProcessing: (data: GameDetectionStatusResponse) =>
      data.isProcessing && data.operation !== null,
    createNotification: (data: GameDetectionStatusResponse) => {
      // `isProcessing` guard above ensures `data.operation !== null` here.
      const op = data.operation!;
      return {
        message: op.statusMessage,
        progress: op.percentComplete,
        details: {
          operationId: op.operationId,
          scanType: op.scanType
        }
      };
    },
    staleMessage: 'Game detection completed'
  } satisfies SimpleRecoveryConfig<GameDetectionStatusResponse>,

  corruptionDetection: {
    apiEndpoint: '/api/cache/corruption/detect/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
    type: 'corruption_detection' as NotificationType,
    notificationId: NOTIFICATION_IDS.CORRUPTION_DETECTION,
    isProcessing: (data: CorruptionDetectionStatusResponse) => data.isRunning,
    createNotification: (data: CorruptionDetectionStatusResponse) => ({
      message: data.stageKey
        ? i18n.t(data.stageKey, data.context ?? {})
        : i18n.t('signalr.corruptionDetect.scanningLogs'),
      // `percentComplete` is not emitted by backend — always undefined on the wire.
      // `?? 0` is a legitimate gap-filler until CacheController.GetCorruptionDetectionStatus
      // is updated to include `percentComplete = activeOp.PercentComplete`.
      progress: data.percentComplete ?? 0,
      details: {
        operationId: data.operationId
      }
    }),
    staleMessage: 'Corruption detection completed'
  } satisfies SimpleRecoveryConfig<CorruptionDetectionStatusResponse>,

  dataImport: {
    apiEndpoint: '/api/migration/import/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.DATA_IMPORT,
    type: 'data_import' as NotificationType,
    notificationId: NOTIFICATION_IDS.DATA_IMPORT,
    isProcessing: (data: DataImportStatusResponse) => data.isProcessing,
    createNotification: (data: DataImportStatusResponse) => ({
      message: data.stageKey
        ? i18n.t(data.stageKey, data.context ?? {})
        : i18n.t('signalr.generic.unknown'),
      // `??` (not `||`): backend field is `double?` — nullable. `??` preserves 0.
      progress: data.percentComplete ?? 0,
      details: {
        operationId: data.operationId ?? undefined
      }
    }),
    staleMessage: 'Data import completed'
  } satisfies SimpleRecoveryConfig<DataImportStatusResponse>,

  epicGameMapping: {
    apiEndpoint: '/api/epic/game-mappings/schedule',
    storageKey: NOTIFICATION_STORAGE_KEYS.EPIC_GAME_MAPPING,
    type: 'epic_game_mapping' as NotificationType,
    notificationId: NOTIFICATION_IDS.EPIC_GAME_MAPPING,
    isProcessing: (data: EpicGameMappingScheduleResponse) => data.isProcessing,
    createNotification: (data: EpicGameMappingScheduleResponse) => ({
      // `statusMessage` is C# `string?` — only populated when processing.
      // Fall back to i18n key when null/undefined (e.g. during idle recovery poll).
      message: data.statusMessage ?? i18n.t('signalr.epicMapping.starting'),
      // `progressPercent` is C# `double` (non-null) — no fallback needed.
      progress: data.progressPercent,
      details: {
        operationId: data.operationId ?? undefined
      }
    }),
    staleMessage: 'Epic game mapping completed'
  } satisfies SimpleRecoveryConfig<EpicGameMappingScheduleResponse>,

  evictionScan: {
    apiEndpoint: '/api/stats/eviction/scan/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.EVICTION_SCAN,
    type: 'eviction_scan' as NotificationType,
    notificationId: NOTIFICATION_IDS.EVICTION_SCAN,
    isProcessing: (data: EvictionScanStatusResponse) => data.isProcessing && !data.silentMode,
    shouldSkip: (data: EvictionScanStatusResponse) => data.isProcessing && data.silentMode,
    createNotification: (data: EvictionScanStatusResponse) => ({
      message: data.stageKey
        ? i18n.t(data.stageKey, data.context ?? {})
        : i18n.t('signalr.evictionScan.scanning'),
      progress: data.percentComplete,
      details: {
        operationId: data.operationId ?? undefined
      }
    }),
    staleMessage: 'Eviction scan completed'
  } satisfies SimpleRecoveryConfig<EvictionScanStatusResponse>
};

// ============================================================================
// Notes
// ============================================================================
// Game Removal, Service Removal, and Corruption Removal are all handled by the
// createCacheRemovalsRecoveryFunction below via /api/cache/removals/active endpoint.

// ============================================================================
// Cache Removals Recovery (handles multiple types)
// ============================================================================

interface CacheRemovalOperation {
  gameAppId?: number;
  gameName?: string;
  serviceName?: string;
  service?: string;
  operationId?: string;
  message?: string;
  startedAt?: string;
  filesDeleted?: number;
  bytesFreed?: number;
}

// REST shape returned by /api/cache/removals/active for eviction_removal entries.
// Uses snake_case because the field names come from [JsonPropertyName] attributes on the C# DTO.
// Note: scope/key/gameName are camelCase here because AllActiveRemovalsResponse uses the global
// JsonNamingPolicy.CamelCase (no [JsonPropertyName] overrides on EvictionRemovalInfo).
interface EvictionRemovalOperation {
  operationId?: string;
  scope?: string; // "steam" | "epic" | "service" | null (bulk)
  key?: string; // steamAppId as string, epicAppId, service name, or null for bulk
  gameName?: string; // resolved display name for steam/epic scopes
  message?: string;
  startedAt?: string;
}

interface CacheRemovalsData {
  isProcessing: boolean;
  gameRemovals?: CacheRemovalOperation[];
  serviceRemovals?: CacheRemovalOperation[];
  corruptionRemovals?: CacheRemovalOperation[];
  evictionRemovals?: EvictionRemovalOperation[];
}

function createCacheRemovalsRecoveryFunction(
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  return async () => {
    try {
      const response = await fetchWithAuth('/api/cache/removals/active');
      if (!response.ok) return;

      const data = (await response.json()) as CacheRemovalsData;

      if (!data.isProcessing) return;

      // Recover game removals
      recoverOperations(
        data.gameRemovals,
        NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        'game_removal',
        () => NOTIFICATION_IDS.GAME_REMOVAL,
        (op) => ({
          message: i18n.t('signalr.gameRemove.starting', {
            gameName: op.gameName ?? '',
            gameAppId: op.gameAppId ?? 0
          }),
          details: {
            gameAppId: op.gameAppId,
            gameName: op.gameName,
            filesDeleted: op.filesDeleted,
            bytesFreed: op.bytesFreed
          }
        }),
        () => NOTIFICATION_IDS.GAME_REMOVAL,
        'Game removal completed',
        setNotifications,
        scheduleAutoDismiss
      );

      // Recover service removals
      recoverOperations(
        data.serviceRemovals,
        NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
        'service_removal',
        () => NOTIFICATION_IDS.SERVICE_REMOVAL,
        (op) => ({
          message: i18n.t('signalr.serviceRemove.starting.default', {
            service: op.serviceName ?? ''
          }),
          details: {
            service: op.serviceName,
            filesDeleted: op.filesDeleted,
            bytesFreed: op.bytesFreed
          }
        }),
        () => NOTIFICATION_IDS.SERVICE_REMOVAL,
        'Service removal completed',
        setNotifications,
        scheduleAutoDismiss
      );

      // Recover corruption removals
      recoverOperations(
        data.corruptionRemovals,
        NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        'corruption_removal',
        () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        (op) => ({
          message: i18n.t('signalr.corruptionRemove.starting', { service: op.service ?? '' }),
          details: {
            operationId: op.operationId,
            service: op.service
          }
        }),
        () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        'Corruption removal completed',
        setNotifications,
        scheduleAutoDismiss
      );

      // Recover eviction removals.
      // Scope-to-identifier mapping (mirrors notificationRegistry.ts getDetails for EvictionRemovalStarted):
      //   steam   → gameAppId: Number(key), steamAppId: key, gameName (optional)
      //   epic    → epicAppId: key, gameName (optional)
      //   service → service: key
      //   null    → bulk removal, no identifier fields needed beyond operationId
      // REST payload uses camelCase (global JsonNamingPolicy.CamelCase on AllActiveRemovalsResponse).
      // SignalR events use camelCase too — but the field semantics differ slightly (see registry comment).
      recoverEvictionRemovals(data.evictionRemovals, setNotifications, scheduleAutoDismiss);
    } catch {
      // Silently fail
    }
  };
}

// Eviction removal recovery is scope-aware and cannot use the generic recoverOperations
// helper because: (1) there is only ever one eviction-removal notification slot (fixed id),
// (2) the details shape differs per scope (steam/epic/service/bulk), and (3) each entry
// already provides operationId which is required for handleCancel to work.
function recoverEvictionRemovals(
  operations: EvictionRemovalOperation[] | undefined,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): void {
  if (operations && operations.length > 0) {
    for (const op of operations) {
      const scope = op.scope?.toLowerCase();
      const key = op.key;

      // Build scope-specific identifier fields to match the notification's details shape
      // produced by notificationRegistry.ts EvictionRemovalStarted.getDetails.
      const scopeDetails: UnifiedNotification['details'] = {
        operationId: op.operationId,
        cancelling: false,
        ...(op.gameName !== undefined && { gameName: op.gameName }),
        ...(scope === 'steam' &&
          key !== undefined && {
            gameAppId: Number(key),
            steamAppId: key
          }),
        ...(scope === 'epic' &&
          key !== undefined && {
            epicAppId: key
          }),
        ...(scope === 'service' &&
          key !== undefined && {
            service: key
          })
      };

      const message =
        op.gameName !== undefined
          ? i18n.t('management.gameDetection.removingGame', { name: op.gameName })
          : scope === 'service' && key !== undefined
            ? i18n.t('signalr.evictionRemove.starting.bulk', {})
            : i18n.t('signalr.evictionRemove.starting.bulk', {});

      const notificationId = NOTIFICATION_IDS.EVICTION_REMOVAL;

      setNotifications((prev: UnifiedNotification[]) => {
        const filtered = prev.filter((n) => n.id !== notificationId);
        return [
          ...filtered,
          {
            id: notificationId,
            type: 'eviction_removal' as const,
            status: 'running' as NotificationStatus,
            message,
            startedAt: op.startedAt ? new Date(op.startedAt) : new Date(),
            details: scopeDetails
          }
        ];
      });
    }
  } else {
    // Clear stale state — always clean up any running eviction_removal notification with no
    // matching active op on the server. The operation completed before the page loaded.
    const saved = localStorage.getItem(NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL);
    if (saved) {
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL);
    }

    setNotifications((prev: UnifiedNotification[]) => {
      const existing = prev.find((n) => n.type === 'eviction_removal' && n.status === 'running');
      if (!existing) return prev;

      const updated = prev.map((n) => {
        if (n.type === 'eviction_removal' && n.status === 'running') {
          return {
            ...n,
            status: 'completed' as NotificationStatus,
            message: i18n.t('signalr.evictionRemove.complete', {}),
            progress: 100
          };
        }
        return n;
      });

      scheduleAutoDismiss(existing.id);
      return updated;
    });
  }
}

function recoverOperations(
  operations: CacheRemovalOperation[] | undefined,
  storageKey: string,
  type: NotificationType,
  getId: (op: CacheRemovalOperation) => string,
  createData: (op: CacheRemovalOperation) => {
    message: string;
    details: UnifiedNotification['details'];
  },
  getIdFromSaved: (saved: UnifiedNotification) => string,
  staleMessage: string,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): void {
  if (operations && operations.length > 0) {
    for (const op of operations) {
      const notificationId = getId(op);
      const data = createData(op);

      setNotifications((prev: UnifiedNotification[]) => {
        const filtered = prev.filter((n) => n.id !== notificationId);
        return [
          ...filtered,
          {
            id: notificationId,
            type,
            status: 'running' as NotificationStatus,
            message: data.message,
            startedAt: op.startedAt ? new Date(op.startedAt) : new Date(),
            details: data.details
          }
        ];
      });
    }
  } else {
    // Clear stale state — always clean up any running notification of this type,
    // regardless of whether localStorage still has the key. This prevents a stuck
    // "running" notification when the completion event cleared localStorage before
    // the app restarted (e.g. SignalR fired completion → removeItem, then page
    // reloaded from an in-memory running state added by a prior recovery call).
    const saved = localStorage.getItem(storageKey);
    if (saved) {
      localStorage.removeItem(storageKey);
    }

    setNotifications((prev: UnifiedNotification[]) => {
      const existing = prev.find((n) => n.type === type && n.status === 'running');
      if (!existing) return prev;

      let recoveryId: string;
      if (saved) {
        try {
          const parsed = JSON.parse(saved) as UnifiedNotification;
          recoveryId = getIdFromSaved(parsed);
        } catch {
          recoveryId = existing.id;
        }
      } else {
        recoveryId = existing.id;
      }

      const updated = prev.map((n) => {
        if (n.type === type && n.status === 'running') {
          return {
            ...n,
            id: recoveryId,
            status: 'completed' as NotificationStatus,
            message: staleMessage,
            progress: 100
          };
        }
        return n;
      });

      scheduleAutoDismiss(recoveryId);
      return updated;
    });
  }
}

// ============================================================================
// Recovery Runner Factory
// ============================================================================

/**
 * Creates a reusable recovery runner function that can be called for both
 * initial page load recovery and SignalR reconnection recovery.
 *
 * This eliminates the duplicated recovery logic that was previously spread
 * across two separate useEffect hooks.
 *
 * @param fetchWithAuth - Authenticated fetch function
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @returns An async function that runs all recovery operations
 *
 * @example
 * ```ts
 * const recoverAllOperations = createRecoveryRunner(fetchWithAuth, setNotifications, scheduleAutoDismiss);
 *
 * // On page load
 * useEffect(() => {
 *   if (isAuthenticated) recoverAllOperations();
 * }, [isAuthenticated]);
 *
 * // On SignalR reconnection
 * useEffect(() => {
 *   if (signalR.connectionState === 'connected' && wasDisconnected) {
 *     recoverAllOperations();
 *   }
 * }, [signalR.connectionState]);
 * ```
 */
export function createRecoveryRunner(
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  const recoverLogProcessing = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.logProcessing,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverCacheClearing = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.cacheClearing,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverDatabaseReset = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.databaseReset,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverDepotMapping = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.depotMapping,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverLogRemoval = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.logRemoval,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverGameDetection = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.gameDetection,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverCorruptionDetection = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.corruptionDetection,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverDataImport = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.dataImport,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverEpicGameMapping = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.epicGameMapping,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverEvictionScan = createSimpleRecoveryFunction(
    RECOVERY_CONFIGS.evictionScan,
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  const recoverCacheRemovals = createCacheRemovalsRecoveryFunction(
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  return async (): Promise<void> => {
    try {
      // Clear all stale notifications before recovery.
      // Recovery functions will re-create only those that are actually active.
      for (const key of Object.values(NOTIFICATION_STORAGE_KEYS)) {
        localStorage.removeItem(key);
      }
      setNotifications([]);

      await Promise.allSettled([
        recoverLogProcessing(),
        recoverLogRemoval(),
        recoverDepotMapping(),
        recoverCacheClearing(),
        recoverDatabaseReset(),
        recoverGameDetection(),
        recoverCorruptionDetection(),
        recoverDataImport(),
        recoverEpicGameMapping(),
        recoverEvictionScan(),
        recoverCacheRemovals()
      ]);
    } catch (err) {
      console.error('[NotificationsContext] Failed to recover operations:', err);
    }
  };
}
