import React, { useState, useCallback, useRef, useMemo, type ReactNode } from 'react';
import { useSignalR } from '../SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useAuth } from '../useAuth';
import themeService from '@services/theme.service';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import type { OperationRunsSnapshot, ShowToastEvent } from '../SignalRContext/types';

import type {
  DispatchDetail,
  LocalNotificationInput,
  NotificationTerminal,
  NotificationsContextType,
  UnifiedNotification
} from './types';
import {
  AUTO_DISMISS_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  LIVE_ONLY_CANCEL_DETAIL_KEYS,
  OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE
} from './constants';
import { isTerminalNotificationStatus } from './notificationStatus';
import { createRecoveryRunner, type FetchWithAuth } from './recovery';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import {
  applyDetail,
  applyRun,
  applySnapshot,
  changeSession,
  createRunStoreState,
  deriveNotifications,
  deriveRuns,
  endStatus,
  hideRun,
  locateRun,
  markConnectionRecovering,
  nextGeneration,
  readOperationRun,
  readOperationRunsSnapshot,
  releaseKeptSuccess,
  removeRuns,
  setRunCancel,
  settleBulkCards,
  type RunApplyResult,
  type RunEntry,
  type RunStoreState
} from './runStore';
import { useNotificationHandlers } from './useNotificationHandlers';

import { NotificationsContext } from './NotificationsContext.types';
import { APP_EVENTS } from '@utils/constants';
import { hasRecentUserInteraction } from '@utils/userInteractionTracker';

interface NotificationsProviderProps {
  children: ReactNode;
}

/** Someone waiting for a run to end: `target` follows the run through every merge. */
interface RunWaiter {
  target: string;
  /** A status request for `target` is on the wire. */
  probing: boolean;
  settle: (terminal: NotificationTerminal) => void;
}

/**
 * Check if notifications should auto-dismiss.
 * Returns true (auto-dismiss enabled) unless "Keep Notifications Visible" is checked.
 *
 * Note: "Disable Sticky Notifications" only controls the sticky position of the
 * notification bar, NOT auto-dismiss behavior.
 */
const shouldAutoDismiss = (): boolean => {
  // Only "Keep Notifications Visible" controls auto-dismiss
  // When checked, notifications stay until manually dismissed
  return !themeService.getPicsAlwaysVisibleSync();
};

// Removal/clearing operation types that share the backend _cacheLock.
// NOTE (wait-queue model): isAnyRemovalRunning must NOT gate buttons whose actions now
// ENQUEUE on conflict - clicking during another op is a supported action whose feedback
// is the purple waiting card. Remaining legitimate consumers are: sync reads that would
// block on _cacheLock (cache-size/log-count refresh), the kept-409 sync corruption scan,
// the client-side bulk-removal loop (not queue-aware), and data-refresh suppression.
const REMOVAL_TYPES = [
  'log_removal',
  'game_removal',
  'service_removal',
  'corruption_removal',
  'cache_clearing'
] as const;

// A failed run-list read is asked again after this long while the connection is up. It repeats a
// request and ends no card; 5 s is the app's retry delay for a failed request
// (ScheduledPrefillEditSessionCleanupRecovery.tsx:6).
const RUN_LIST_RETRY_DELAY_MS = 5000;

export const NotificationsProvider: React.FC<NotificationsProviderProps> = ({ children }) => {
  // The run store and the browser's own cards live in refs so every SignalR and HTTP handler
  // applies its change synchronously; one counter tells React to draw the result.
  const storeRef = useRef<RunStoreState>(createRunStoreState());
  const localRef = useRef<UnifiedNotification[]>([]);
  const [version, setVersion] = useState(0);
  const waitersRef = useRef(new Set<RunWaiter>());
  const requestSeqRef = useRef(0);
  const appliedSeqRef = useRef(0);
  const resyncPendingRef = useRef(false);
  const fadingRef = useRef(new Set<string>());
  const autoDismissTimersRef = useRef(new Map<string, ReturnType<typeof setTimeout>>());
  const { invoke, isConnected } = useSignalR();
  const { authMode, isLoading: authLoading, sessionId } = useAuth();
  const isAdmin = !authLoading && authMode === 'authenticated';
  const isAdminRef = useRef(isAdmin);
  isAdminRef.current = isAdmin;
  const isConnectedRef = useRef(isConnected);
  isConnectedRef.current = isConnected;
  const snapshotRetryRef = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const sessionIdRef = useRef(sessionId);
  sessionIdRef.current = sessionId;
  const sessionBeforeRef = useRef(sessionId);
  const connectedBeforeRef = useRef(isConnected);

  const commit = useCallback(() => setVersion((current) => current + 1), []);

  // The card's exit fade: the removal was already decided; this is only the 300 ms the bar
  // animates before the card is gone.
  const fadeLocal = useCallback(
    (id: string) => {
      // A batch card is released again by every apply until its fade ends.
      if (fadingRef.current.has(id)) return;
      const card = localRef.current.find((n) => n.id === id);
      fadingRef.current.add(id);
      window.dispatchEvent(
        new CustomEvent(APP_EVENTS.NOTIFICATION_REMOVING, { detail: { notificationId: id } })
      );
      setTimeout(() => {
        fadingRef.current.delete(id);
        localRef.current = localRef.current.filter((n) => n !== card);
        commit();
      }, NOTIFICATION_ANIMATION_DURATION_MS);
    },
    [commit]
  );

  // A run card leaves because its row said it ended. It stays for the one popup time first, like
  // every card that leaves on its own, then fades; a card closed on another screen fades at once.
  // Each run's hold and then its fade live in one timer keyed by the run, so a newer run on the
  // same card never meets an older run's timer, and the unmount clears them all.
  const fadeLeavingRuns = useCallback(() => {
    const timers = autoDismissTimersRef.current;
    for (const entry of storeRef.current.entries.values()) {
      const operationId = entry.run.operationId;
      if (!entry.leaving || timers.has(operationId)) continue;
      const closed = entry.run.closed === true;
      timers.set(
        operationId,
        setTimeout(
          () => {
            const leaving = storeRef.current.entries.get(operationId);
            // Keep Notifications Visible turned on during the hold keeps the card.
            if (!leaving?.leaving || (!closed && !shouldAutoDismiss())) {
              timers.delete(operationId);
              return;
            }
            window.dispatchEvent(
              new CustomEvent(APP_EVENTS.NOTIFICATION_REMOVING, {
                detail: { notificationId: leaving.cardId }
              })
            );
            timers.set(
              operationId,
              setTimeout(() => {
                timers.delete(operationId);
                if (!storeRef.current.entries.get(operationId)?.leaving) return;
                storeRef.current = removeRuns(storeRef.current, [operationId]);
                commit();
              }, NOTIFICATION_ANIMATION_DURATION_MS)
            );
          },
          closed ? 0 : AUTO_DISMISS_DELAY_MS
        )
      );
    }
  }, [commit]);

  const scheduleAutoDismiss = useCallback(
    (notificationId: string) => {
      const timers = autoDismissTimersRef.current;
      clearTimeout(timers.get(notificationId));
      timers.set(
        notificationId,
        setTimeout(() => {
          timers.delete(notificationId);
          const card = localRef.current.find((n) => n.id === notificationId);
          // A bulk card ends by the ending rules: a failed one stays until closed. Keep
          // Notifications Visible turned on during the wait keeps the card.
          if (
            card &&
            shouldAutoDismiss() &&
            isTerminalNotificationStatus(card.status) &&
            !(card.type === 'bulk_removal' && card.status === 'failed')
          )
            fadeLocal(notificationId);
        }, AUTO_DISMISS_DELAY_MS)
      );
    },
    [fadeLocal]
  );

  const settleBulk = useCallback(() => {
    const settled = settleBulkCards(storeRef.current, localRef.current);
    storeRef.current = settled.next;
    settled.release.forEach(fadeLocal);
  }, [fadeLocal]);

  // Follows a waiter to its run's current id and settles it once that run ended. A run this
  // browser has never heard of is asked about directly, so no waiter depends on elapsed time.
  const followWaiter = useMemo(() => {
    function follow(waiter: RunWaiter, probe: boolean): void {
      const place = locateRun(storeRef.current, waiter.target);
      waiter.target = place.operationId;
      if (place.terminal) waiter.settle(place.terminal);
      else if (!place.known && probe) void probeRun(waiter);
    }
    async function probeRun(waiter: RunWaiter): Promise<void> {
      const target = waiter.target;
      waiter.probing = true;
      try {
        const answer = await ApiService.getTrackedOperation(target);
        waiter.probing = false;
        if (locateRun(storeRef.current, target).known) {
          follow(waiter, false);
        } else if (answer.nextOperationId) {
          waiter.target = answer.nextOperationId;
          follow(waiter, true);
        } else if (!answer.status) {
          waiter.settle({ operationId: target, status: 'gone' });
        } else if (isTerminalNotificationStatus(answer.status)) {
          waiter.settle({
            operationId: target,
            status: endStatus(answer.status),
            error: answer.error ?? undefined
          });
        }
        // A live answer keeps the waiter until the next snapshot that does not hold the run.
      } catch (error: unknown) {
        waiter.probing = false;
        if (error instanceof ApiError && error.status === 404)
          waiter.settle({ operationId: target, status: 'gone' });
      }
    }
    return follow;
  }, []);

  const commitApply = useCallback(
    (result: RunApplyResult, probe: boolean) => {
      storeRef.current = result.next;
      const waiters = [...waitersRef.current];
      for (const { from, to } of result.merged)
        for (const waiter of waiters) if (waiter.target === from) waiter.target = to;
      for (const end of result.ended)
        for (const waiter of waiters) if (waiter.target === end.operationId) waiter.settle(end);
      for (const waiter of waitersRef.current) if (!waiter.probing) followWaiter(waiter, probe);
      settleBulk();
      fadeLeavingRuns();
      commit();
    },
    [followWaiter, settleBulk, fadeLeavingRuns, commit]
  );

  // Authenticated fetch helper for snapshot and recovery requests - they fire from a
  // SignalR-reconnect effect with no user action involved (a network blip or laptop wake can
  // reconnect while the tab is genuinely idle), so it carries the same activity signal as
  // ApiService.getFetchOptions() rather than keeping LastSeenAtUtc artificially fresh.
  const fetchWithAuth: FetchWithAuth = useCallback(async (url: string): Promise<Response> => {
    return fetch(url, {
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        'X-User-Active': hasRecentUserInteraction(120_000) ? 'true' : 'false'
      }
    });
  }, []);

  const recover = useMemo(() => createRecoveryRunner(fetchWithAuth), [fetchWithAuth]);

  // Fills in the text of runs whose card shows only its row. Each result applies only while
  // nothing newer reached its run since this request started.
  const recoverDetails = useCallback(
    async (operationIds: readonly string[]): Promise<void> => {
      const targets = operationIds.flatMap((id) => {
        const entry = storeRef.current.entries.get(id);
        return entry ? [entry] : [];
      });
      if (targets.length === 0) return;
      const requestSeq = ++requestSeqRef.current;
      const generation = storeRef.current.generation;
      const revisions = new Map(
        targets.map((entry) => [entry.run.operationId, entry.detailRevision])
      );
      const types = new Set(
        targets.map((entry) => OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[entry.run.operationType])
      );
      const complete = await recover(types, (type, operationId, detail) => {
        // A result without an operation id (a reset recovered before its id was registered)
        // belongs to the one live run of its type, or to none when there are several.
        const matches = [...storeRef.current.entries.values()].filter(
          (entry) =>
            revisions.has(entry.run.operationId) &&
            (operationId
              ? entry.aliases.includes(operationId)
              : OPERATION_WIRE_TYPE_TO_NOTIFICATION_TYPE[entry.run.operationType] === type)
        );
        if (matches.length !== 1) return;
        const key = matches[0].run.operationId;
        storeRef.current = applyDetail(storeRef.current, key, () => detail, 'recovery', {
          requestSeq,
          generation,
          detailRevision: revisions.get(key)
        });
      });
      if (!complete) resyncPendingRef.current = true;
      commit();
    },
    [recover, commit]
  );

  const requestSnapshot = useCallback(async (): Promise<void> => {
    clearTimeout(snapshotRetryRef.current);
    const requestSeq = ++requestSeqRef.current;
    const generation = storeRef.current.generation;
    // The hub adds a connection to the admin group inside OnConnectedAsync, which finishes before
    // any hub call is dispatched, so a snapshot sent after this call settles was captured after
    // the join; a rejected call means the connection dropped, and its reconnect asks again.
    await invoke('JoinAuthenticatedGroupAsync').catch(() => undefined);
    let snapshot: OperationRunsSnapshot | null = null;
    try {
      const response = await fetchWithAuth('/api/operations/runs');
      if (response.ok) snapshot = readOperationRunsSnapshot(await response.json());
    } catch (error: unknown) {
      console.warn('Unable to read the run list', error);
    }
    if (!snapshot) {
      resyncPendingRef.current = true;
      // A run that ended while this browser could not read the list keeps its card, or its closed
      // card, and its waiters busy until a read succeeds, so ask again. The reconnect asks for itself.
      snapshotRetryRef.current = setTimeout(() => {
        if (isAdminRef.current && isConnectedRef.current) void requestSnapshot();
      }, RUN_LIST_RETRY_DELAY_MS);
      return;
    }
    if (generation !== storeRef.current.generation || requestSeq < appliedSeqRef.current) return;
    appliedSeqRef.current = requestSeq;
    resyncPendingRef.current = false;
    const result = applySnapshot(storeRef.current, snapshot, {
      keepSuccessVisible: !shouldAutoDismiss(),
      localCards: localRef.current,
      requestSeq,
      issuedSeq: requestSeqRef.current,
      sessionId: sessionIdRef.current
    });
    commitApply(result, true);
    void recoverDetails(result.recover);
  }, [invoke, fetchWithAuth, commitApply, recoverDetails]);

  const handleRun = useCallback(
    (value: unknown): void => {
      const resync = resyncPendingRef.current;
      const row = readOperationRun(value);
      if (!row) {
        resyncPendingRef.current = true;
        void requestSnapshot();
        return;
      }
      const successorId = row.nextOperationId;
      const linkedBefore =
        !!successorId && !!storeRef.current.links.get(successorId)?.includes(row.operationId);
      const result = applyRun(storeRef.current, row, {
        keepSuccessVisible: !shouldAutoDismiss(),
        localCards: localRef.current,
        pushed: true,
        sessionId: sessionIdRef.current
      });
      commitApply(result, false);
      // A handoff to a successor this browser has not seen: the snapshot says where the work went.
      const linked =
        !!successorId && !!result.next.links.get(successorId)?.includes(row.operationId);
      if (resync || (linked && !linkedBefore)) void requestSnapshot();
    },
    [requestSnapshot, commitApply]
  );

  const dispatchDetail: DispatchDetail = useCallback(
    (operationId, build, source) => {
      const before = storeRef.current;
      storeRef.current = applyDetail(before, operationId, build, source, {
        requestSeq: requestSeqRef.current
      });
      if (storeRef.current === before) return;
      // A prefill event from a series not yet adopted waits for the run-status response.
      const pending = storeRef.current.entries.get(operationId)?.stream?.pending;
      if (pending && pending !== before.entries.get(operationId)?.stream?.pending)
        void recoverDetails([operationId]);
      commit();
    },
    [recoverDetails, commit]
  );

  const showAnnouncement = useCallback(
    (card: Omit<UnifiedNotification, 'id' | 'startedAt'>) => {
      // One card per announcement type: a newer one replaces the card instead of stacking.
      const id = card.type;
      localRef.current = [
        ...localRef.current.filter((n) => n.id !== id),
        { ...card, id, startedAt: new Date() }
      ];
      if (card.status === 'completed') scheduleAutoDismiss(id);
      commit();
    },
    [scheduleAutoDismiss, commit]
  );

  const addNotification = useCallback(
    (notification: LocalNotificationInput): string => {
      // A generic card's id comes from its message, so the same message never stacks twice.
      const id =
        notification.type === 'generic' && notification.message
          ? `generic_${notification.message.replace(/[^a-zA-Z0-9]/g, '_').substring(0, 50)}`
          : `notification_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
      localRef.current = [
        ...localRef.current.filter((n) => n.id !== id),
        { ...notification, id, startedAt: new Date() }
      ];
      if (isTerminalNotificationStatus(notification.status)) scheduleAutoDismiss(id);
      commit();
      return id;
    },
    [scheduleAutoDismiss, commit]
  );

  const updateNotification = useCallback(
    (
      id: string,
      updates:
        | Partial<UnifiedNotification>
        | ((notification: UnifiedNotification) => Partial<UnifiedNotification>)
    ) => {
      const local = localRef.current.find((n) => n.id === id);
      if (local) {
        const patch = typeof updates === 'function' ? updates(local) : updates;
        if (
          isTerminalNotificationStatus(local.status) &&
          patch.status &&
          !isTerminalNotificationStatus(patch.status)
        )
          return;
        const card: UnifiedNotification = {
          ...local,
          ...patch,
          id: local.id,
          startedAt: local.startedAt
        };
        localRef.current = localRef.current.map((n) => (n === local ? card : n));
        if (
          card.type === 'bulk_removal' &&
          !isTerminalNotificationStatus(local.status) &&
          isTerminalNotificationStatus(card.status)
        ) {
          // A finished batch follows the run ending rules: a success or a cancel stays for the
          // popup time, then leaves, unless Keep Notifications Visible holds it; a failure stays
          // until closed.
          scheduleAutoDismiss(id);
          settleBulk();
        }
        commit();
        return;
      }
      const drawn = [
        ...deriveNotifications(storeRef.current, localRef.current),
        ...deriveRuns(storeRef.current)
      ].find((n) => n.id === id);
      if (!drawn) return;
      const patch = typeof updates === 'function' ? updates(drawn) : updates;
      // The server owns a run's state; the browser writes only its cancel intent.
      const cancel: RunEntry['cancel'] = {};
      for (const key of LIVE_ONLY_CANCEL_DETAIL_KEYS)
        if (patch.details && key in patch.details) cancel[key] = patch.details[key];
      if (patch.status === 'cancelling') cancel.cancelling = true;
      storeRef.current = setRunCancel(storeRef.current, id, cancel);
      commit();
    },
    [scheduleAutoDismiss, settleBulk, commit]
  );

  const removeNotification = useCallback(
    (id: string, closedOperationIds?: string[]) => {
      clearTimeout(autoDismissTimersRef.current.get(id));
      autoDismissTimersRef.current.delete(id);
      const local = localRef.current.some((n) => n.id === id);
      localRef.current = localRef.current.filter((n) => n.id !== id);
      // The ids the server confirmed closed are recorded as ended, so their closed rows change
      // nothing; a kept run a batch card listed but the server did not close becomes its own card.
      storeRef.current = removeRuns(
        storeRef.current,
        local ? (closedOperationIds ?? []) : [id, ...(closedOperationIds ?? [])]
      );
      for (const waiter of waitersRef.current) if (!waiter.probing) followWaiter(waiter, false);
      commit();
    },
    [followWaiter, commit]
  );

  const hideNotification = useCallback(
    (id: string) => {
      storeRef.current = hideRun(storeRef.current, id);
      commit();
    },
    [commit]
  );

  const waitForRunEnd = useCallback(
    (operationId: string, signal?: AbortSignal): Promise<NotificationTerminal> =>
      new Promise<NotificationTerminal>((resolve) => {
        const abort = (): void => waiter.settle({ operationId, status: 'gone' });
        const waiter: RunWaiter = {
          target: operationId,
          probing: false,
          settle: (terminal) => {
            if (!waitersRef.current.delete(waiter)) return;
            signal?.removeEventListener('abort', abort);
            resolve(terminal);
          }
        };
        waitersRef.current.add(waiter);
        if (signal?.aborted) {
          abort();
          return;
        }
        signal?.addEventListener('abort', abort);
        followWaiter(waiter, true);
      }),
    [followWaiter]
  );

  useNotificationHandlers(NOTIFICATION_REGISTRY, dispatchDetail, showAnnouncement, handleRun);

  // Toast notifications: every one leaves after the one popup time, which addNotification starts
  // for every finished card.
  React.useEffect(() => {
    const handleShowToast = (e: Event) => {
      const { message, type, error } = (e as CustomEvent<ShowToastEvent>).detail;
      addNotification({
        type: 'generic',
        status: type === 'error' ? 'failed' : 'completed',
        message,
        error,
        details: {
          notificationType: type
        }
      });
    };

    window.addEventListener(APP_EVENTS.SHOW_TOAST, handleShowToast);
    return () => window.removeEventListener(APP_EVENTS.SHOW_TOAST, handleShowToast);
  }, [addNotification]);

  // "Keep Notifications Visible" turned off: the successes and cancels it held stay for the popup
  // time, then leave. Failed, skipped and warning cards stay until closed, and so does the Steam
  // session error.
  React.useEffect(() => {
    const handleNotificationVisibilityChange = () => {
      if (!shouldAutoDismiss()) return;
      storeRef.current = releaseKeptSuccess(storeRef.current);
      for (const card of localRef.current)
        if (isTerminalNotificationStatus(card.status) && card.type !== 'steam_session_error')
          scheduleAutoDismiss(card.id);
      // A leaving run card's hold starts over from now, as a held popup's does.
      const timers = autoDismissTimersRef.current;
      for (const entry of storeRef.current.entries.values())
        if (entry.leaving) {
          clearTimeout(timers.get(entry.run.operationId));
          timers.delete(entry.run.operationId);
        }
      fadeLeavingRuns();
      commit();
    };

    window.addEventListener(
      APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE,
      handleNotificationVisibilityChange
    );
    return () =>
      window.removeEventListener(
        APP_EVENTS.NOTIFICATION_VISIBILITY_CHANGE,
        handleNotificationVisibilityChange
      );
  }, [scheduleAutoDismiss, fadeLeavingRuns, commit]);

  React.useEffect(
    () => () => {
      for (const timerId of autoDismissTimersRef.current.values()) clearTimeout(timerId);
      autoDismissTimersRef.current.clear();
      clearTimeout(snapshotRetryRef.current);
    },
    []
  );

  // Snapshot on mount (admin-only - the run list requires admin access).
  React.useEffect(() => {
    if (isAdmin) void requestSnapshot();
  }, [isAdmin, requestSnapshot]);

  // A dropped connection may lose prefill events; the card says so until detail arrives again.
  React.useEffect(() => {
    if (isConnected) return;
    storeRef.current = markConnectionRecovering(storeRef.current);
    commit();
  }, [isConnected, commit]);

  // Every connect: rows sent while disconnected are gone, and after a server restart the new
  // server numbers its rows from 1, so a reconnect starts a new generation before it asks.
  useReconnectRefetch(isConnected, () => {
    if (connectedBeforeRef.current) storeRef.current = nextGeneration(storeRef.current);
    connectedBeforeRef.current = true;
    if (isAdminRef.current) void requestSnapshot();
  });

  // Signed out or signed in as someone else: the earlier session's sign-in cards leave now, since a
  // sign-out asks for no run list and a failed or slow read applies nothing.
  React.useEffect(() => {
    if (sessionBeforeRef.current === sessionId) return;
    sessionBeforeRef.current = sessionId;
    storeRef.current = changeSession(storeRef.current, sessionId);
    commit();
    if (isAdminRef.current) void requestSnapshot();
  }, [sessionId, commit, requestSnapshot]);

  // Tab return: the browser may have throttled or dropped message processing while hidden.
  React.useEffect(() => {
    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible' && isAdminRef.current) void requestSnapshot();
    };
    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, [requestSnapshot]);

  const notifications = useMemo(
    () => deriveNotifications(storeRef.current, localRef.current),
    // `version` is the store's change counter; the store itself lives in a ref.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [version]
  );
  const runs = useMemo(
    () => deriveRuns(storeRef.current),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [version]
  );

  // Compute if any removal operation is running (these all share a backend lock)
  const isAnyRemovalRunning = useMemo(
    () =>
      runs.some(
        (n) =>
          REMOVAL_TYPES.includes(n.type as (typeof REMOVAL_TYPES)[number]) && n.status === 'running'
      ),
    [runs]
  );

  const value: NotificationsContextType = {
    notifications,
    runs,
    addNotification,
    updateNotification,
    removeNotification,
    hideNotification,
    isAnyRemovalRunning,
    scheduleAutoDismiss,
    waitForRunEnd
  };

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
};
