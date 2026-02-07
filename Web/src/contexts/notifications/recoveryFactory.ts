import type {
  NotificationType,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss
} from './types';
import { NOTIFICATION_STORAGE_KEYS, NOTIFICATION_IDS } from './constants';
import {
  formatLogProcessingRecoveryMessage,
  formatLogProcessingRecoveryDetailMessage,
  formatDepotMappingRecoveryDetailMessage
} from './detailMessageFormatters';

export type FetchWithAuth = (url: string) => Promise<Response>;

// ============================================================================
// Simple Recovery Config (for fixed-ID operations)
// ============================================================================

interface SimpleRecoveryConfig {
  apiEndpoint: string;
  storageKey: string;
  type: NotificationType;
  notificationId: string;
  isProcessing: (data: Record<string, unknown>) => boolean;
  shouldSkip?: (data: Record<string, unknown>) => boolean;
  createNotification: (data: Record<string, unknown>) => Omit<UnifiedNotification, 'id' | 'type' | 'status' | 'startedAt'>;
  staleMessage: string;
}

function createSimpleRecoveryFunction(
  config: SimpleRecoveryConfig,
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  return async () => {
    try {
      const response = await fetchWithAuth(config.apiEndpoint);
      if (!response.ok) return;

      const data = await response.json();
      const notificationId = config.notificationId;

      // Check if we should skip (e.g., silent mode)
      if (config.shouldSkip?.(data)) {
        localStorage.removeItem(config.storageKey);
        setNotifications((prev: UnifiedNotification[]) => prev.filter((n) => n.type !== config.type));
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
        // Clear stale state
        const saved = localStorage.getItem(config.storageKey);
        if (saved) {
          localStorage.removeItem(config.storageKey);

          setNotifications((prev: UnifiedNotification[]) => {
            const existing = prev.find((n) => n.type === config.type && n.status === 'running');
            if (existing) {
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
            }
            return prev;
          });

          scheduleAutoDismiss(notificationId);
        }
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
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing) && !data.silentMode,
    shouldSkip: (data: Record<string, unknown>) => Boolean(data.isProcessing) && Boolean(data.silentMode),
    createNotification: (data: Record<string, unknown>) => ({
      message: formatLogProcessingRecoveryMessage(data.mbProcessed as number, data.mbTotal as number),
      detailMessage: formatLogProcessingRecoveryDetailMessage(data.entriesProcessed as number, data.totalLines as number),
      progress: Math.min(99.9, (data.percentComplete as number) || 0),
      details: {
        mbProcessed: data.mbProcessed as number,
        mbTotal: data.mbTotal as number,
        entriesProcessed: data.entriesProcessed as number,
        totalLines: data.totalLines as number
      }
    }),
    staleMessage: 'Processing Complete!'
  } satisfies SimpleRecoveryConfig,

  cacheClearing: {
    apiEndpoint: '/api/cache/operations',
    storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
    type: 'cache_clearing' as NotificationType,
    notificationId: NOTIFICATION_IDS.CACHE_CLEARING,
    isProcessing: (data: Record<string, unknown>) => {
      const ops = data.operations as Record<string, unknown>[] | undefined;
      return Boolean(data.isProcessing) && Boolean(ops?.length);
    },
    createNotification: (data: Record<string, unknown>) => {
      const ops = data.operations as Record<string, unknown>[];
      const activeOp = ops?.[0] || {};
      return {
        message: (activeOp.statusMessage as string) || 'Clearing cache...',
        progress: (activeOp.percentComplete as number) || 0,
        details: {
          filesDeleted: (activeOp.filesDeleted as number) || 0,
          directoriesProcessed: (activeOp.directoriesProcessed as number) || 0,
          bytesDeleted: (activeOp.bytesDeleted as number) || 0
        }
      };
    },
    staleMessage: 'Cache clearing completed'
  } satisfies SimpleRecoveryConfig,

  databaseReset: {
    apiEndpoint: '/api/database/reset-status',
    storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
    type: 'database_reset' as NotificationType,
    notificationId: NOTIFICATION_IDS.DATABASE_RESET,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing),
    createNotification: (data: Record<string, unknown>) => ({
      message: (data.message as string) || 'Resetting database...',
      progress: (data.percentComplete as number) || 0
    }),
    staleMessage: 'Database reset completed'
  } satisfies SimpleRecoveryConfig,

  depotMapping: {
    apiEndpoint: '/api/depots/rebuild/progress',
    storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
    type: 'depot_mapping' as NotificationType,
    notificationId: NOTIFICATION_IDS.DEPOT_MAPPING,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing),
    createNotification: (data: Record<string, unknown>) => {
      const detailMessage = formatDepotMappingRecoveryDetailMessage({
        processedBatches: data.processedBatches as number | undefined,
        totalBatches: data.totalBatches as number | undefined,
        depotMappingsFound: data.depotMappingsFound as number | undefined
      });

      return {
        message: (data.statusMessage as string) || 'Downloading depot data...',
        detailMessage,
        progress: (data.percentComplete as number) || 0,
        details: {
          operationId: data.operationId as string | undefined,
          totalMappings: data.totalMappings as number,
          processedMappings: data.processedMappings as number,
          isLoggedOn: data.isLoggedOn as boolean,
          percentComplete: data.percentComplete as number
        }
      };
    },
    staleMessage: 'Depot mapping completed'
  } satisfies SimpleRecoveryConfig,

  logRemoval: {
    apiEndpoint: '/api/logs/remove/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
    type: 'log_removal' as NotificationType,
    notificationId: NOTIFICATION_IDS.LOG_REMOVAL,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing) && Boolean(data.service),
    createNotification: (data: Record<string, unknown>) => ({
      message: `Removing ${data.service} entries from logs`,
      progress: (data.percentComplete as number) || 0,
      details: {
        service: data.service as string,
        linesProcessed: data.linesProcessed as number,
        linesRemoved: data.linesRemoved as number
      }
    }),
    staleMessage: 'Log entry removal completed'
  } satisfies SimpleRecoveryConfig,

  gameDetection: {
    apiEndpoint: '/api/games/detect/active',
    storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
    type: 'game_detection' as NotificationType,
    notificationId: NOTIFICATION_IDS.GAME_DETECTION,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing) && Boolean(data.operation),
    createNotification: (data: Record<string, unknown>) => {
      const op = data.operation as Record<string, unknown>;
      return {
        message: (op?.statusMessage as string) || 'Detecting games and services in cache...',
        progress: (op?.percentComplete as number) || 0,
        details: {
          operationId: op?.operationId as string,
          scanType: op?.scanType as 'full' | 'incremental'
        }
      };
    },
    staleMessage: 'Game detection completed'
  } satisfies SimpleRecoveryConfig,

  corruptionDetection: {
    apiEndpoint: '/api/cache/corruption/detect/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
    type: 'corruption_detection' as NotificationType,
    notificationId: NOTIFICATION_IDS.CORRUPTION_DETECTION,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isRunning),
    createNotification: (data: Record<string, unknown>) => ({
      message: (data.message as string) || 'Scanning for corrupted cache chunks...',
      progress: (data.percentComplete as number) || 0,
      details: {
        operationId: data.operationId as string
      }
    }),
    staleMessage: 'Corruption detection completed'
  } satisfies SimpleRecoveryConfig,

  dataImport: {
    apiEndpoint: '/api/migration/import/status',
    storageKey: NOTIFICATION_STORAGE_KEYS.DATA_IMPORT,
    type: 'data_import' as NotificationType,
    notificationId: NOTIFICATION_IDS.DATA_IMPORT,
    isProcessing: (data: Record<string, unknown>) => Boolean(data.isProcessing),
    createNotification: (data: Record<string, unknown>) => ({
      message: (data.message as string) || 'Importing data...',
      progress: (data.percentComplete as number) || 0,
      details: {
        operationId: data.operationId as string
      }
    }),
    staleMessage: 'Data import completed'
  } satisfies SimpleRecoveryConfig
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

interface CacheRemovalsData {
  isProcessing: boolean;
  gameRemovals?: CacheRemovalOperation[];
  serviceRemovals?: CacheRemovalOperation[];
  corruptionRemovals?: CacheRemovalOperation[];
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
          message: op.message || `Removing ${op.gameName}...`,
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
          message: op.message || `Removing ${op.serviceName} service...`,
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
          message: op.message || `Removing corrupted chunks for ${op.service}...`,
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
    } catch {
      // Silently fail
    }
  };
}

function recoverOperations(
  operations: CacheRemovalOperation[] | undefined,
  storageKey: string,
  type: NotificationType,
  getId: (op: CacheRemovalOperation) => string,
  createData: (op: CacheRemovalOperation) => { message: string; details: UnifiedNotification['details'] },
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
    // Clear stale state
    const saved = localStorage.getItem(storageKey);
    if (saved) {
      localStorage.removeItem(storageKey);

      let parsed: UnifiedNotification;
      try {
        parsed = JSON.parse(saved);
      } catch {
        return;
      }

      const recoveryId = getIdFromSaved(parsed);

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.type === type && n.status === 'running');
        if (existing) {
          return prev.map((n) => {
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
        }
        return prev;
      });

      scheduleAutoDismiss(recoveryId);
    }
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

  const recoverCacheRemovals = createCacheRemovalsRecoveryFunction(
    fetchWithAuth,
    setNotifications,
    scheduleAutoDismiss
  );

  return async (): Promise<void> => {
    try {
      await Promise.allSettled([
        recoverLogProcessing(),
        recoverLogRemoval(),
        recoverDepotMapping(),
        recoverCacheClearing(),
        recoverDatabaseReset(),
        recoverGameDetection(),
        recoverCorruptionDetection(),
        recoverDataImport(),
        recoverCacheRemovals()
      ]);
    } catch (err) {
      console.error('[NotificationsContext] Failed to recover operations:', err);
    }
  };
}
