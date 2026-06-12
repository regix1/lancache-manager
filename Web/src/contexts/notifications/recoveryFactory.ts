import type {
  NotificationType,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  NotificationRegistryEntry,
  SimpleRecoveryConfig
} from './types';
import {
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE
} from './constants';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import i18n from '@/i18n';

export type FetchWithAuth = (url: string) => Promise<Response>;

/** Row shape of GET /api/operations/waiting (wait-queue recovery endpoint). */
interface WaitingOperationRow {
  operationId: string;
  operationType: string;
  name: string;
}

/**
 * Builds the wait-queue recovery function: synchronizes purple "waiting" cards with the
 * backend queue. Creates missing waiting cards (with details.operationId so cancel works)
 * and removes waiting cards whose queued op no longer exists.
 */
function createWaitingOperationsRecoveryFunction(
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications
): () => Promise<void> {
  return async () => {
    try {
      const response = await fetchWithAuth('/api/operations/waiting');
      if (!response.ok) return;

      const rows = (await response.json()) as WaitingOperationRow[];
      const waitingByType = new Map<NotificationType, WaitingOperationRow>();
      for (const row of rows) {
        const type = OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[row.operationType];
        if (type) waitingByType.set(type, row);
      }

      setNotifications((prev: UnifiedNotification[]) => {
        // Drop stale waiting cards (op promoted or cancelled while we weren't listening).
        const next = prev.filter((n) => n.status !== 'waiting' || waitingByType.has(n.type));

        // Create cards for queued ops that have none (and whose slot isn't already a
        // running card - a promoted op's card must not be downgraded back to waiting).
        for (const entry of NOTIFICATION_REGISTRY as NotificationRegistryEntry[]) {
          const row = waitingByType.get(entry.type);
          if (!row) continue;
          if (next.some((n) => n.id === entry.id)) continue;
          next.push({
            id: entry.id,
            type: entry.type,
            status: 'waiting' as NotificationStatus,
            message: i18n.t('common.notifications.operationWaiting'),
            startedAt: new Date(),
            details: { operationId: row.operationId }
          });
        }

        return next;
      });
    } catch {
      // Silently fail - recovery is best-effort
    }
  };
}

// ============================================================================
// Simple Recovery Engine (for fixed-ID operations)
// ============================================================================
// Each per-type SimpleRecoveryConfig lives on its registry entry
// (notificationRegistry.ts). The runner pairs the config with the entry's
// type / id / storageKey and builds a recovery function with this engine.
//
// The createNotification/isProcessing/shouldSkip readers in the config access
// REST response property names directly (snake_case/camelCase as the wire
// delivers them) and are intentionally NOT normalized against SignalR event
// property names — a field may cross both boundaries with different casing.

function createSimpleRecoveryFunction<TData>(
  config: SimpleRecoveryConfig<TData>,
  type: NotificationType,
  notificationId: string,
  storageKey: string,
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  return async () => {
    try {
      const response = await fetchWithAuth(config.apiEndpoint);
      if (!response.ok) return;

      const data = (await response.json()) as TData;

      // Check if we should skip (e.g., silent mode)
      if (config.shouldSkip?.(data)) {
        localStorage.removeItem(storageKey);
        setNotifications((prev: UnifiedNotification[]) => prev.filter((n) => n.type !== type));
        return;
      }

      if (config.isProcessing(data)) {
        const notificationData = config.createNotification(data);
        setNotifications((prev: UnifiedNotification[]) => {
          const filtered = prev.filter((n) => n.type !== type);
          return [
            ...filtered,
            {
              id: notificationId,
              type,
              status: 'running' as NotificationStatus,
              startedAt: new Date(),
              ...notificationData
            }
          ];
        });
      } else {
        // Clear stale localStorage entry if present
        const saved = localStorage.getItem(storageKey);
        if (saved) {
          localStorage.removeItem(storageKey);
        }

        // Always transition any running notification of this type to completed.
        // This handles both:
        // 1. Notifications restored from localStorage (stale from previous session)
        // 2. Notifications created by a previous recovery poll (when isProcessing was true)
        //    - these don't use localStorage, so the old `if (saved)` guard missed them
        setNotifications((prev: UnifiedNotification[]) => {
          const existing = prev.find((n) => n.type === type && n.status === 'running');
          if (!existing) return prev;

          return prev.map((n) => {
            if (n.type === type && n.status === 'running') {
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
// Cache Removals Recovery (handles multiple types via ONE endpoint)
// ============================================================================
// game_removal, service_removal, corruption_removal, and eviction_removal are
// all recovered by a SINGLE GET to /api/cache/removals/active. Their registry
// entries carry `recovery: { kind: 'cacheRemovalsBatch' }` as a marker; the
// runner issues this fetch exactly once for the whole group.

interface CacheRemovalOperation {
  gameAppId?: number | null;
  epicAppId?: string | null;
  entityKind?: 'steam' | 'epic' | null;
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
// scope/key/gameName are camelCase because AllActiveRemovalsResponse uses the global
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

      // NOTE: no top-level `if (!data.isProcessing) return;` here. When the server reports
      // no active processing, the per-type branches below must still run so their else
      // (empty-array) branches stale-complete any stuck `running` card for game/service/
      // corruption/eviction removal - exactly how createSimpleRecoveryFunction self-heals.
      // recoverOperations / recoverEvictionRemovals already transition running→completed +
      // scheduleAutoDismiss when their op array is empty. data.isProcessing===false implies
      // every op array is empty/absent, so each branch takes its clear path.

      // Recover game removals.
      // Post-Phase-2 contract: game_removal rehydrates scope-aware identity. Steam entries
      // emit details.gameAppId (number); Epic entries emit details.epicAppId (string). The
      // `?? 0` fallback is gone per acceptance criterion 3.7 - ops missing both identity
      // fields are logged and skipped (legacy/pre-Phase-2 data only).
      const recoverableGameRemovals = (data.gameRemovals ?? []).filter((op) => {
        if ((op.entityKind === 'epic' || op.epicAppId) && op.epicAppId) return true;
        if (typeof op.gameAppId === 'number') return true;
        console.warn('[recovery] Skipping game_removal op with no scope identity:', op.operationId);
        return false;
      });
      recoverOperations(
        recoverableGameRemovals,
        NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        'game_removal',
        () => NOTIFICATION_IDS.GAME_REMOVAL,
        (op) => {
          const isEpic = op.entityKind === 'epic' || !!op.epicAppId;
          const stageKey = isEpic ? 'signalr.epicRemove.starting' : 'signalr.gameRemove.starting';
          const context = {
            gameName: op.gameName ?? '',
            ...(typeof op.gameAppId === 'number' && { gameAppId: op.gameAppId }),
            ...(op.epicAppId && { epicAppId: op.epicAppId })
          };
          const baseDetails = {
            operationId: op.operationId,
            gameName: op.gameName ?? '',
            stageKey,
            filesDeleted: op.filesDeleted,
            bytesFreed: op.bytesFreed
          };
          const details =
            isEpic && op.epicAppId
              ? { ...baseDetails, epicAppId: op.epicAppId }
              : typeof op.gameAppId === 'number'
                ? { ...baseDetails, gameAppId: op.gameAppId }
                : baseDetails;
          return {
            message: i18n.t(stageKey, context),
            details
          };
        },
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
            operationId: op.operationId,
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
      // SignalR events use camelCase too - but the field semantics differ slightly (see registry comment).
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
          : scope !== undefined && key !== undefined
            ? i18n.t('signalr.evictionRemove.starting.entity', { scope, key })
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
    // Clear stale state - always clean up any running eviction_removal notification with no
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
    // Clear stale state - always clean up any running notification of this type,
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
 * The runner is registry-driven: it walks NOTIFICATION_REGISTRY and, per entry's
 * `recovery` discriminated union, builds the appropriate recovery function:
 *   - kind:'simple' → createSimpleRecoveryFunction(entry.recovery, type, id, storageKey)
 *   - kind:'cacheRemovalsBatch' → covered by a SINGLE createCacheRemovalsRecoveryFunction
 *     run (one GET to /api/cache/removals/active for the whole group)
 *   - kind:'none' → no recovery
 *
 * @param fetchWithAuth - Authenticated fetch function
 * @param setNotifications - React setState function for notifications
 * @param scheduleAutoDismiss - Function to schedule auto-dismissal
 * @returns An async function that runs all recovery operations
 */
export function createRecoveryRunner(
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss
): () => Promise<void> {
  const recoveryFns: (() => Promise<void>)[] = [];
  let needsCacheRemovalsBatch = false;

  for (const entry of NOTIFICATION_REGISTRY as NotificationRegistryEntry[]) {
    switch (entry.recovery.kind) {
      case 'simple':
        recoveryFns.push(
          createSimpleRecoveryFunction(
            entry.recovery,
            entry.type,
            entry.id,
            entry.storageKey,
            fetchWithAuth,
            setNotifications,
            scheduleAutoDismiss
          )
        );
        break;
      case 'cacheRemovalsBatch':
        // All cacheRemovalsBatch entries share ONE /api/cache/removals/active
        // fetch; collapse them into a single recovery run below.
        needsCacheRemovalsBatch = true;
        break;
      case 'none':
        break;
    }
  }

  if (needsCacheRemovalsBatch) {
    recoveryFns.push(
      createCacheRemovalsRecoveryFunction(fetchWithAuth, setNotifications, scheduleAutoDismiss)
    );
  }

  // Operation wait-queue: recreate purple waiting cards from /api/operations/waiting on
  // page load / reconnect / tab-revisible, and drop stale waiting cards whose op vanished
  // (promoted ops are re-created as running cards by the per-type engines above; cancelled
  // ones are simply gone). Queued ops do NOT survive an app restart - after a restart the
  // endpoint returns [] and no cards are created, by design.
  recoveryFns.push(createWaitingOperationsRecoveryFunction(fetchWithAuth, setNotifications));

  return async (): Promise<void> => {
    try {
      await Promise.allSettled(recoveryFns.map((fn) => fn()));
    } catch (err) {
      console.error('[NotificationsContext] Failed to recover operations:', err);
    }
  };
}
