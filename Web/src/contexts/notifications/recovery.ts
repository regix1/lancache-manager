import type {
  NotificationType,
  NotificationStatus,
  UnifiedNotification,
  SetNotifications,
  ScheduleAutoDismiss,
  SimpleRecoveryConfig,
  NotificationEvents,
  CancelAutoDismissTimer
} from './types';
import {
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE,
  LIVE_ONLY_CANCEL_DETAIL_KEYS,
  FULL_PROGRESS_PERCENT,
  GENERIC_FAILURE_I18N_KEY,
  REMOVING_GAME_I18N_KEY
} from './constants';
import {
  findBulkCardOwningOperation,
  waitingCardMessage,
  operationCardId,
  applyHandoff,
  applyPredecessor,
  rememberEvent,
  persistNotification,
  clearPersistedNotificationIfTargeted
} from './handlers';
import { isTerminalNotificationStatus } from './notificationStatus';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import { classifyRemovalKind, removalStageKey, withRemovalIdentity } from './removalKind';
import i18n from '@/i18n';
import type { CorruptionDetectionMethod } from '@/types';
import type { RefObject } from 'react';
import type { OperationStatusResponse } from './recoveryStatusResponses';

export type FetchWithAuth = (url: string) => Promise<Response>;

interface RecoveryPass {
  startedAt: Date;
  revision: number;
  starting: readonly UnifiedNotification[];
  events: NotificationEvents;
  changed: Set<NotificationType>;
  cancelAutoDismissTimer: CancelAutoDismissTimer;
}

function canRecover(
  pass: RecoveryPass | undefined,
  type: NotificationType,
  _operationId?: string
): boolean {
  return !pass || !pass.changed.has(type);
}

// ============================================================================
// Recovered-card reconciliation
// ============================================================================
// Recovery does NOT only run on page load: it also runs on every SignalR transition to
// 'connected' AND every time the tab becomes visible again after >2s hidden. At those moments
// the operation is usually still running and its card is LIVE, carrying state that only SignalR
// ever delivers - progress, detailMessage, progressMode, progressAriaValueText.
//
// Rebuilding that card from the REST snapshot destroyed every field the snapshot does not carry,
// which is why the progress bar vanished whenever the user switched tabs: renderProgressBar
// draws nothing once progress is undefined. It stayed gone because nothing repairs the card until
// the NEXT progress event, and some operations are deliberately quiet for long stretches (the
// scheduled-prefill poll only re-emits when its message changes or the percent moves a full
// point, which during one large game download can be tens of minutes).
//
// The rule below is: recovery may only overwrite what it actually knows.

/**
 * True when a snapshot and a live card describe the same operation. A snapshot that omits the
 * operationId cannot prove the card is a DIFFERENT run - and each of these types owns a single
 * card slot - so an unknown id is treated as the same operation rather than as grounds to
 * destroy live state.
 */
function isSameOperation(
  existing: UnifiedNotification,
  recoveredOperationId: string | undefined
): boolean {
  const existingOperationId = existing.details?.operationId;
  if (!existingOperationId || !recoveredOperationId) return true;
  return existingOperationId === recoveredOperationId;
}

/**
 * Recovered details that may be merged onto a live card: undefined values are dropped (a snapshot
 * that omits a field must not erase it - notably `operationId: undefined` would break cancel), and
 * live-only cancel flags are stripped.
 */
function mergeableDetails(
  details: UnifiedNotification['details']
): NonNullable<UnifiedNotification['details']> {
  if (!details) return {};
  const liveOnly = LIVE_ONLY_CANCEL_DETAIL_KEYS as readonly string[];
  return Object.fromEntries(
    Object.entries(details).filter(([key, value]) => value !== undefined && !liveOnly.includes(key))
  );
}

/**
 * Reconciles a REST recovery snapshot against the card already on screen.
 *
 *  - No live running card, or a DIFFERENT operationId (a genuine re-run) -> the snapshot is the
 *    card, seeded fresh exactly as before.
 *  - Same operation, snapshot carries NO progress -> the endpoint reports identity only
 *    (scheduled-prefill run-status and /api/cache/removals/active both do). Keep the live card and
 *    only fill in details it lacks, so its bar and stage text survive.
 *  - Same operation, snapshot carries progress -> the snapshot is authoritative for the fields it
 *    supplies; every field it omits stays on the live card.
 */
function reconcileRecoveredCard(
  existing: UnifiedNotification | undefined,
  recovered: UnifiedNotification
): UnifiedNotification {
  if (!existing || !isSameOperation(existing, recovered.details?.operationId)) {
    return recovered;
  }
  if (isTerminalNotificationStatus(existing.status)) return existing;

  const merged: UnifiedNotification = {
    ...existing,
    controlOnly: recovered.controlOnly,
    status: existing.details?.cancelRequested ? 'cancelling' : recovered.status,
    details: { ...existing.details, ...mergeableDetails(recovered.details) }
  };

  if (recovered.progress === undefined) {
    return merged;
  }

  return {
    ...merged,
    message: recovered.message,
    progress: recovered.progress,
    detailMessage: recovered.detailMessage ?? existing.detailMessage,
    progressMode: recovered.progressMode ?? existing.progressMode,
    progressAriaValueText: recovered.progressAriaValueText ?? existing.progressAriaValueText
  };
}

/** Row shape of GET /api/operations/waiting (wait-queue recovery endpoint). */
interface WaitingOperationRow {
  showNotification?: boolean;
  status?: string;
  operationId: string;
  operationType: string;
  name: string;
  /** Display name of the operation this one is parked behind; null when unknown. */
  blockedByName?: string | null;
  startedAt?: string;
}

/**
 * Builds the wait-queue recovery function: synchronizes purple "waiting" cards with the
 * backend queue. Creates missing waiting cards (with details.operationId so cancel works)
 * and removes waiting cards whose queued op no longer exists. When a batch already reports on
 * a queued item's type, its own card carries the wording instead of a second card appearing.
 */
function createWaitingOperationsRecoveryFunction(
  fetchWithAuth: FetchWithAuth,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss = () => undefined,
  pass?: RecoveryPass
): () => Promise<void> {
  return async () => {
    const startedAt = pass?.startedAt ?? new Date();
    try {
      const response = await fetchWithAuth('/api/operations/waiting');
      if (response.status === 401 || response.status === 403) return;
      if (!response.ok)
        throw new Error(`Unable to recover waiting operations (${response.status})`);
      const rows = (await response.json()) as WaitingOperationRow[];
      const probes = new Map<
        string,
        { status: OperationStatusResponse; target?: OperationStatusResponse }
      >();
      const missing = new Set(
        (pass?.starting ?? [])
          .filter((n) => n.status === 'waiting' || n.status === 'cancelling')
          .map((n) => n.details?.currentOperationId ?? n.details?.operationId)
          .filter(
            (id): id is string =>
              !!id && !rows.some((row) => row.operationId === id) && !pass?.events.handoffs.has(id)
          )
      );
      await Promise.all(
        [...missing].map(async (id) => {
          const result = await fetchWithAuth(`/api/operations/${encodeURIComponent(id)}`);
          if (result.status === 401 || result.status === 403) return;
          if (!result.ok) throw new Error(`Unable to recover waiting operation (${result.status})`);
          const status = (await result.json()) as OperationStatusResponse;
          let target: OperationStatusResponse | undefined;
          if (
            status.nextOperationId &&
            status.nextOperationId !== id &&
            (!status.nextStatus ||
              ['completed', 'failed', 'cancelled', 'skipped'].includes(status.nextStatus))
          ) {
            const successor = await fetchWithAuth(
              `/api/operations/${encodeURIComponent(status.nextOperationId)}`
            );
            if (successor.status !== 401 && successor.status !== 403) {
              if (!successor.ok)
                throw new Error(`Unable to recover promoted operation (${successor.status})`);
              target = (await successor.json()) as OperationStatusResponse;
            }
          }
          probes.set(id, { status, target });
        })
      );
      setNotifications((prev) => {
        let next = prev.filter((n) => n.id !== 'recovery_waiting');
        for (const n of prev) {
          const operationId = n.details?.currentOperationId ?? n.details?.operationId;
          if (!operationId || !canRecover(pass, n.type, operationId)) continue;
          const entry = NOTIFICATION_REGISTRY.find((candidate) => candidate.type === n.type);
          if (!entry || !pass) continue;
          const probe = probes.get(operationId);
          let handoff = pass.events.handoffs.get(operationId);
          if (
            !handoff &&
            probe?.status.nextOperationId &&
            probe.status.nextOperationId !== operationId
          ) {
            handoff = {
              operationId,
              operationType:
                Object.entries(OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE).find(
                  ([, type]) => type === n.type
                )?.[0] ?? '',
              promoted: true,
              cancelled: false,
              nextOperationId: probe.status.nextOperationId,
              nextStatus: probe.target?.status ?? probe.status.nextStatus ?? undefined,
              error: probe.target?.error ?? undefined
            };
            rememberEvent(pass.events, n.type, 'handoff', 'OperationWaitingComplete', handoff);
            if (probe.target?.status && isTerminalNotificationStatus(probe.target.status)) {
              const id = handoff.nextOperationId!;
              if (!pass.events.terminals.has(id))
                pass.events.terminals.set(id, {
                  operationId: id,
                  status: probe.target.status as 'completed' | 'failed' | 'cancelled' | 'skipped',
                  error: probe.target.error ?? undefined
                });
            }
          }
          if (handoff)
            next = applyHandoff(
              next,
              handoff,
              pass.events,
              entry,
              scheduleAutoDismiss,
              pass.cancelAutoDismissTimer
            );
        }
        next = next.filter((n) => {
          if (
            n.type === 'bulk_removal' ||
            n.status !== 'waiting' ||
            !canRecover(pass, n.type, n.details?.operationId) ||
            rows.some((row) => row.operationId === n.details?.operationId)
          )
            return true;
          const operationId = n.details?.operationId;
          if (operationId && probes.get(operationId)?.status.active) return true;
          const entry = NOTIFICATION_REGISTRY.find((candidate) => candidate.type === n.type);
          if (entry)
            clearPersistedNotificationIfTargeted(
              entry.storageKey,
              { operationId },
              n.id,
              entry.getId !== undefined
            );
          return false;
        });
        for (const row of rows) {
          const type = OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[row.operationType];
          if (
            !type ||
            !canRecover(pass, type, row.operationId) ||
            pass?.events.terminals.has(row.operationId)
          )
            continue;
          const entry = NOTIFICATION_REGISTRY.find((candidate) => candidate.type === type);
          if (!entry || type === 'scheduled_prefill') continue;
          if (next.some((n) => n.type !== 'generic' && n.details?.operationId === row.operationId))
            continue;
          const owningBulk = findBulkCardOwningOperation(type, row.operationId, next);
          if (owningBulk) {
            next = next.map((n) =>
              n === owningBulk
                ? {
                    ...n,
                    status: 'waiting',
                    message: waitingCardMessage(row),
                    details: { ...n.details, currentOperationId: row.operationId }
                  }
                : n
            );
            continue;
          }
          if (row.showNotification !== false && next.some((n) => n.id === entry.id)) continue;
          const date = new Date(row.startedAt ?? startedAt);
          next.push({
            id: row.showNotification === false ? operationCardId(row.operationId) : entry.id,
            type,
            status: row.status === 'cancelling' ? 'cancelling' : 'waiting',
            controlOnly: row.showNotification === false,
            message: waitingCardMessage(row),
            startedAt: Number.isFinite(date.getTime()) ? date : startedAt,
            details: { operationId: row.operationId }
          });
          pass?.events.waiting.add(row.operationId);
        }
        return next;
      });
    } catch (error: unknown) {
      setNotifications((prev) => {
        if (pass && pass.events.revision !== pass.revision) return prev;
        if (prev.some((n) => n.id === 'recovery_waiting')) return prev;
        scheduleAutoDismiss('recovery_waiting');
        return [
          ...prev,
          {
            id: 'recovery_waiting',
            type: 'generic',
            status: 'failed',
            message: error instanceof Error ? error.message : i18n.t(GENERIC_FAILURE_I18N_KEY),
            startedAt,
            details: { notificationType: 'error' }
          }
        ];
      });
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
  scheduleAutoDismiss: ScheduleAutoDismiss,
  pass?: RecoveryPass
): () => Promise<void> {
  return async () => {
    const startedAt = pass?.startedAt ?? new Date();
    const errorId = `recovery_${notificationId}`;
    try {
      const response = await fetchWithAuth(config.apiEndpoint);
      if (response.status === 401 || response.status === 403) return;
      if (!response.ok) throw new Error(`Unable to recover operation (${response.status})`);
      const data = (await response.json()) as TData;
      const outcome = ((data as { operation?: unknown }).operation ?? data) as {
        status?: NotificationStatus;
        error?: string;
        message?: string;
        operationId?: string;
        parentOperationId?: string | null;
        startedAt?: string;
        startTime?: string;
      };
      const operationId = outcome.operationId;
      if (
        type === 'game_detection' &&
        operationId &&
        (outcome.parentOperationId || pass?.events.children.has(operationId))
      ) {
        setNotifications((prev) => {
          if (!canRecover(pass, type, operationId)) return prev;
          if (outcome.parentOperationId)
            pass?.events.children.set(operationId, outcome.parentOperationId);
          clearPersistedNotificationIfTargeted(storageKey, { operationId }, notificationId);
          return prev.filter(
            (n) => n.id !== errorId && !(n.type === type && n.details?.operationId === operationId)
          );
        });
        return;
      }
      if (outcome.status && isTerminalNotificationStatus(outcome.status) && operationId) {
        setNotifications((prev) => {
          if (!canRecover(pass, type, operationId)) return prev;
          const next = prev.filter((n) => n.id !== errorId);
          const existing = next.find(
            (n) => n.type === type && n.details?.operationId === operationId
          );
          if (existing && isTerminalNotificationStatus(existing.status)) return next;
          if (
            !existing &&
            (outcome.status !== 'failed' ||
              pass?.events.terminals.get(operationId)?.presented ||
              next.some((n) => n.id === notificationId))
          )
            return next;
          const known = pass?.events.terminals.get(operationId);
          const status = known?.status ?? outcome.status!;
          const entry = NOTIFICATION_REGISTRY.find((n) => n.type === type);
          const message =
            known?.error ??
            outcome.error ??
            outcome.message ??
            i18n.t(status === 'failed' ? GENERIC_FAILURE_I18N_KEY : config.staleMessageKey);
          const card: UnifiedNotification = {
            ...existing,
            id: existing?.id ?? notificationId,
            type,
            status,
            message,
            controlOnly: undefined,
            error: status === 'failed' ? message : undefined,
            progress: status === 'skipped' ? undefined : FULL_PROGRESS_PERCENT,
            startedAt: existing?.startedAt ?? startedAt,
            details: { ...existing?.details, operationId },
            detailMessage: entry?.complete?.getDetailMessage?.(data) ?? existing?.detailMessage
          };
          if (!known && pass) {
            pass.events.terminals.set(operationId, {
              operationId,
              status: status as 'completed' | 'failed' | 'cancelled' | 'skipped',
              error: outcome.error
            });
            pass.events.acknowledgedIds.add(`terminal_${operationId}`);
          }
          clearPersistedNotificationIfTargeted(
            storageKey,
            { operationId },
            card.id,
            !!config.recoverCards
          );
          scheduleAutoDismiss(card.id);
          return existing ? next.map((n) => (n === existing ? card : n)) : [...next, card];
        });
        return;
      }
      if (config.shouldSkip?.(data)) {
        setNotifications((prev) => {
          if (!canRecover(pass, type, operationId)) return prev;
          return prev.filter((n) => {
            if (n.id === errorId) return false;
            if (
              n.type !== type ||
              isTerminalNotificationStatus(n.status) ||
              (operationId && n.details?.operationId !== operationId)
            )
              return true;
            clearPersistedNotificationIfTargeted(
              storageKey,
              { operationId: n.details?.operationId },
              n.id,
              !!config.recoverCards
            );
            return false;
          });
        });
        return;
      }
      if (config.isProcessing(data)) {
        const cards = config.recoverCards
          ? config.recoverCards(data)
          : [{ id: notificationId, ...config.createNotification(data) }];
        setNotifications((prev) => {
          if (!canRecover(pass, type, operationId)) return prev;
          let next = prev.filter((n) => n.id !== errorId);
          for (const snapshot of cards) {
            const id = snapshot.details?.operationId;
            const entry = NOTIFICATION_REGISTRY.find((candidate) => candidate.type === type);
            if (pass && entry)
              next = applyPredecessor(
                next,
                snapshot.details,
                pass.events,
                entry,
                scheduleAutoDismiss,
                pass.cancelAutoDismissTimer
              );
            if (!canRecover(pass, type, id) || (id && pass?.events.terminals.has(id))) continue;
            if (type === 'game_detection' && snapshot.details?.parentOperationId) {
              if (id) {
                pass?.events.children.set(id, snapshot.details.parentOperationId);
                clearPersistedNotificationIfTargeted(
                  storageKey,
                  { operationId: id },
                  snapshot.id,
                  !!config.recoverCards
                );
                next = next.filter((n) => n.type !== type || n.details?.operationId !== id);
              }
              continue;
            }
            if (id && findBulkCardOwningOperation(type, id, next)) continue;
            const exact = id
              ? next.find((n) => n.type === type && n.details?.operationId === id)
              : undefined;
            const cardId =
              exact?.id ?? (snapshot.controlOnly && id ? operationCardId(id) : snapshot.id);
            const slot = next.find((n) => n.id === cardId);
            const existing = exact ?? slot;
            if (existing && existing.status === 'waiting' && existing.details?.operationId !== id)
              continue;
            const date = new Date(outcome.startedAt ?? outcome.startTime ?? startedAt);
            const recovered: UnifiedNotification = {
              type,
              status: 'running',
              startedAt: Number.isFinite(date.getTime()) ? date : startedAt,
              ...snapshot,
              id: cardId
            };
            const card = reconcileRecoveredCard(existing, recovered);
            if (existing && card !== existing && existing.details?.operationId !== id)
              clearPersistedNotificationIfTargeted(
                storageKey,
                { operationId: existing.details?.operationId },
                existing.id,
                !!config.recoverCards
              );
            persistNotification(storageKey, card, !!config.recoverCards);
            next = existing ? next.map((n) => (n === existing ? card : n)) : [...next, card];
          }
          return next;
        });
      } else {
        setNotifications((prev) => {
          if (!canRecover(pass, type, operationId)) return prev;
          return prev
            .filter((n) => n.id !== errorId)
            .flatMap((n) => {
              if (n.type !== type || (n.status !== 'running' && n.status !== 'cancelling'))
                return [n];
              if (operationId && n.details?.operationId && n.details.operationId !== operationId)
                return [n];
              clearPersistedNotificationIfTargeted(
                storageKey,
                { operationId: n.details?.operationId },
                n.id,
                !!config.recoverCards
              );
              if (n.controlOnly) return [];
              scheduleAutoDismiss(n.id);
              return [
                {
                  ...n,
                  status: 'completed' as const,
                  message: i18n.t(config.staleMessageKey),
                  progress: FULL_PROGRESS_PERCENT
                }
              ];
            });
        });
      }
    } catch (error: unknown) {
      setNotifications((prev) => {
        if (!canRecover(pass, type) || prev.some((n) => n.id === errorId)) return prev;
        scheduleAutoDismiss(errorId);
        return [
          ...prev,
          {
            id: errorId,
            type: 'generic',
            status: 'failed',
            message: error instanceof Error ? error.message : i18n.t(GENERIC_FAILURE_I18N_KEY),
            startedAt,
            details: { notificationType: 'error' }
          }
        ];
      });
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
  entityKind?: 'steam' | 'epic' | 'named' | null;
  gameName?: string;
  serviceName?: string;
  service?: string;
  operationId?: string;
  message?: string;
  startedAt?: string;
  filesDeleted?: number;
  bytesFreed?: number;
  status?: string;
  detectionMethod?: CorruptionDetectionMethod;
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
  scheduleAutoDismiss: ScheduleAutoDismiss,
  pass?: RecoveryPass
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
      // emit details.gameAppId (number); Epic entries emit details.epicAppId (string); named
      // entries (blizzard/riot/xbox, entityKind==='named') emit neither - identity lives in
      // gameName/service, not an appId. Ops missing ALL THREE (no epicAppId, no positive
      // gameAppId, no entityKind:'named') are legacy/pre-Phase-2 data only - logged and skipped.
      const recoverableGameRemovals = (data.gameRemovals ?? []).filter((op) => {
        if ((op.entityKind === 'epic' || op.epicAppId) && op.epicAppId) return true;
        if (typeof op.gameAppId === 'number' && op.gameAppId > 0) return true;
        if (op.entityKind === 'named') return true;
        console.warn('[recovery] Skipping game_removal op with no scope identity:', op.operationId);
        return false;
      });
      recoverOperations(
        recoverableGameRemovals,
        NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        'game_removal',
        () => NOTIFICATION_IDS.GAME_REMOVAL,
        (op) => {
          // Named (blizzard/riot/xbox): neither epic nor a positive Steam AppId. Reuses the
          // existing signalr.namedRemove.* family (already wired into GamesController's
          // Started/Complete events for this same path) - AppID-free, matches epicRemove shape.
          const kind = classifyRemovalKind(op);
          const stageKey = removalStageKey(kind, 'starting');
          const context = withRemovalIdentity(
            { gameName: op.gameName ?? '' },
            kind,
            op.gameAppId,
            op.epicAppId
          );
          const baseDetails = {
            operationId: op.operationId,
            gameName: op.gameName ?? '',
            stageKey,
            filesDeleted: op.filesDeleted,
            bytesFreed: op.bytesFreed
          };
          const details = withRemovalIdentity(baseDetails, kind, op.gameAppId, op.epicAppId);
          return {
            message: i18n.t(stageKey, context),
            details
          };
        },
        i18n.t('signalr.gameRemove.stale'),
        setNotifications,
        scheduleAutoDismiss,
        pass
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
        i18n.t('signalr.serviceRemove.stale'),
        setNotifications,
        scheduleAutoDismiss,
        pass
      );

      // Recover corruption removals
      recoverOperations(
        data.corruptionRemovals,
        NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        'corruption_removal',
        () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        (op) => ({
          message: i18n.t(
            op.detectionMethod === 'structural'
              ? 'signalr.corruptionRemove.startingStructural'
              : 'signalr.corruptionRemove.starting',
            { service: op.service ?? '' }
          ),
          details: {
            operationId: op.operationId,
            service: op.service,
            detectionMethod: op.detectionMethod
          }
        }),
        i18n.t('signalr.corruptionRemove.stale'),
        setNotifications,
        scheduleAutoDismiss,
        pass
      );

      // Recover eviction removals.
      // Scope-to-identifier mapping (mirrors notificationRegistry.ts getDetails for EvictionRemovalStarted):
      //   steam   → gameAppId: Number(key), steamAppId: key, gameName (optional)
      //   epic    → epicAppId: key, gameName (optional)
      //   service → service: key
      //   null    → bulk removal, no identifier fields needed beyond operationId
      // REST payload uses camelCase (global JsonNamingPolicy.CamelCase on AllActiveRemovalsResponse).
      // SignalR events use camelCase too - but the field semantics differ slightly (see registry comment).
      recoverEvictionRemovals(data.evictionRemovals, setNotifications, scheduleAutoDismiss, pass);
    } catch (error: unknown) {
      // Active-removal recovery is best effort; its operation cards own visible failures.
      console.warn('Unable to recover active removals', {
        endpoint: '/api/cache/removals/active',
        error
      });
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
  scheduleAutoDismiss: ScheduleAutoDismiss,
  pass?: RecoveryPass
): void {
  const startedAt = pass?.startedAt ?? new Date();
  const storageKey = NOTIFICATION_STORAGE_KEYS.EVICTION_REMOVAL;
  setNotifications((prev) => {
    if (!canRecover(pass, 'eviction_removal')) return prev;
    let next = prev;
    if (operations?.length) {
      for (const op of operations) {
        if (op.operationId && pass?.events.terminals.has(op.operationId)) continue;
        if (op.operationId && findBulkCardOwningOperation('eviction_removal', op.operationId, next))
          continue;
        const scope = op.scope?.toLowerCase();
        const key = op.key;
        const exact = op.operationId
          ? next.find(
              (n) => n.type === 'eviction_removal' && n.details?.operationId === op.operationId
            )
          : undefined;
        const existing = exact ?? next.find((n) => n.id === NOTIFICATION_IDS.EVICTION_REMOVAL);
        if (existing?.status === 'waiting' && existing.details?.operationId !== op.operationId)
          continue;
        const date = new Date(op.startedAt ?? startedAt);
        const card = reconcileRecoveredCard(existing, {
          id: exact?.id ?? NOTIFICATION_IDS.EVICTION_REMOVAL,
          type: 'eviction_removal',
          status: 'running',
          startedAt: Number.isFinite(date.getTime()) ? date : startedAt,
          message:
            op.gameName !== undefined
              ? i18n.t(REMOVING_GAME_I18N_KEY, { name: op.gameName })
              : scope !== undefined && key !== undefined
                ? i18n.t('signalr.evictionRemove.starting.entity', { scope, key })
                : i18n.t('signalr.evictionRemove.starting.bulk', {}),
          details: {
            operationId: op.operationId,
            ...(op.gameName !== undefined && { gameName: op.gameName }),
            ...(scope === 'steam' &&
              key !== undefined && { gameAppId: Number(key), steamAppId: key }),
            ...(scope === 'epic' && key !== undefined && { epicAppId: key }),
            ...(scope === 'service' && key !== undefined && { service: key })
          }
        });
        if (existing && existing.details?.operationId !== op.operationId)
          clearPersistedNotificationIfTargeted(
            storageKey,
            { operationId: existing.details?.operationId },
            existing.id
          );
        persistNotification(storageKey, card);
        next = existing ? next.map((n) => (n === existing ? card : n)) : [...next, card];
      }
      return next;
    }
    return next.map((n) => {
      if (n.type !== 'eviction_removal' || (n.status !== 'running' && n.status !== 'cancelling'))
        return n;
      clearPersistedNotificationIfTargeted(
        storageKey,
        { operationId: n.details?.operationId },
        n.id
      );
      scheduleAutoDismiss(n.id);
      return {
        ...n,
        status: 'completed',
        message: i18n.t('signalr.evictionRemove.complete', {}),
        progress: FULL_PROGRESS_PERCENT
      };
    });
  });
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
  staleMessage: string,
  setNotifications: SetNotifications,
  scheduleAutoDismiss: ScheduleAutoDismiss,
  pass?: RecoveryPass
): void {
  const startedAt = pass?.startedAt ?? new Date();
  setNotifications((prev) => {
    if (!canRecover(pass, type)) return prev;
    let next = prev;
    if (operations?.length) {
      for (const op of operations) {
        if (op.operationId && pass?.events.terminals.has(op.operationId)) continue;
        if (op.operationId && findBulkCardOwningOperation(type, op.operationId, next)) continue;
        const exact = op.operationId
          ? next.find((n) => n.type === type && n.details?.operationId === op.operationId)
          : undefined;
        const existing = exact ?? next.find((n) => n.id === getId(op));
        if (existing?.status === 'waiting' && existing.details?.operationId !== op.operationId)
          continue;
        const date = new Date(op.startedAt ?? startedAt);
        const card = reconcileRecoveredCard(existing, {
          ...createData(op),
          id: exact?.id ?? getId(op),
          type,
          status: 'running',
          startedAt: Number.isFinite(date.getTime()) ? date : startedAt
        });
        if (existing && existing.details?.operationId !== op.operationId)
          clearPersistedNotificationIfTargeted(
            storageKey,
            { operationId: existing.details?.operationId },
            existing.id
          );
        persistNotification(storageKey, card);
        next = existing ? next.map((n) => (n === existing ? card : n)) : [...next, card];
      }
      return next;
    }
    return next.map((n) => {
      if (n.type !== type || (n.status !== 'running' && n.status !== 'cancelling')) return n;
      clearPersistedNotificationIfTargeted(
        storageKey,
        { operationId: n.details?.operationId },
        n.id
      );
      scheduleAutoDismiss(n.id);
      return { ...n, status: 'completed', message: staleMessage, progress: FULL_PROGRESS_PERCENT };
    });
  });
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
  scheduleAutoDismiss: ScheduleAutoDismiss,
  events: RefObject<NotificationEvents>,
  getNotifications: () => readonly UnifiedNotification[],
  cancelAutoDismissTimer: CancelAutoDismissTimer = () => undefined
): () => Promise<void> {
  let inFlight: Promise<void> | null = null;
  let trailing = false;
  const recover = async (): Promise<void> => {
    const pass: RecoveryPass = {
      startedAt: new Date(),
      revision: events.current.revision,
      starting: [...getNotifications()],
      events: events.current,
      changed: new Set(),
      cancelAutoDismissTimer
    };
    const groups: { updates: Parameters<SetNotifications>[0][]; run: () => Promise<void> }[] = [];
    const waiting: Parameters<SetNotifications>[0][] = [];
    groups.push({
      updates: waiting,
      run: createWaitingOperationsRecoveryFunction(
        fetchWithAuth,
        (update) => {
          waiting.push(update);
        },
        scheduleAutoDismiss,
        pass
      )
    });
    let needsCacheRemovalsBatch = false;
    for (const entry of NOTIFICATION_REGISTRY) {
      if (entry.recovery.kind === 'cacheRemovalsBatch') needsCacheRemovalsBatch = true;
      if (entry.recovery.kind !== 'simple') continue;
      const updates: Parameters<SetNotifications>[0][] = [];
      groups.push({
        updates,
        run: createSimpleRecoveryFunction(
          entry.recovery,
          entry.type,
          entry.id,
          entry.storageKey,
          fetchWithAuth,
          (update) => {
            updates.push(update);
          },
          scheduleAutoDismiss,
          pass
        )
      });
    }
    if (needsCacheRemovalsBatch) {
      const updates: Parameters<SetNotifications>[0][] = [];
      groups.push({
        updates,
        run: createCacheRemovalsRecoveryFunction(
          fetchWithAuth,
          (update) => {
            updates.push(update);
          },
          scheduleAutoDismiss,
          pass
        )
      });
    }
    await Promise.all(groups.map((group) => group.run()));
    await new Promise<void>((resolve) => {
      let committedPrev: UnifiedNotification[] | undefined;
      let committedNext: UnifiedNotification[] | undefined;
      setNotifications((prev) => {
        if (prev === committedPrev && committedNext) return committedNext;
        for (const [type, revision] of events.current.typeRevisions) {
          if (revision > pass.revision) pass.changed.add(type);
        }
        for (const card of prev) {
          if (!pass.starting.includes(card)) pass.changed.add(card.type);
        }
        for (const card of pass.starting) {
          if (!prev.includes(card)) pass.changed.add(card.type);
        }
        let next = prev;
        for (const group of groups) {
          for (const update of group.updates)
            next = typeof update === 'function' ? update(next) : update;
        }
        committedPrev = prev;
        committedNext = next;
        queueMicrotask(resolve);
        return next;
      });
    });
  };
  return (): Promise<void> => {
    if (inFlight) {
      trailing = true;
      return inFlight;
    }
    inFlight = (async () => {
      try {
        await recover();
        if (trailing) {
          trailing = false;
          await recover();
        }
      } finally {
        trailing = false;
        inFlight = null;
      }
    })();
    return inFlight;
  };
}
