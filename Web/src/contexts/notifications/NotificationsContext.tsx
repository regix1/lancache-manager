import React, { useState, useCallback, useRef, useMemo, type ReactNode } from 'react';
import { useSignalR } from '../SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useAuth } from '../useAuth';
import themeService from '@services/theme.service';
import type { ShowToastEvent } from '../SignalRContext/types';

import type { UnifiedNotification, NotificationEvents } from './types';
import {
  AUTO_DISMISS_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  TOAST_DEFAULT_DURATION_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS,
  LIVE_ONLY_CANCEL_DETAIL_KEYS
} from './constants';
import { isTerminalNotificationStatus } from './notificationStatus';
import { createRecoveryRunner, type FetchWithAuth } from './recovery';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import {
  readPersistedCards,
  clearPersistedNotificationIfTargeted,
  persistNotification
} from './handlers';
import { useNotificationHandlers } from './useNotificationHandlers';

import { NotificationsContext } from './NotificationsContext.types';
import { APP_EVENTS } from '@utils/constants';
import { hasRecentUserInteraction } from '@utils/userInteractionTracker';

interface NotificationsProviderProps {
  children: ReactNode;
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

export const NotificationsProvider: React.FC<NotificationsProviderProps> = ({ children }) => {
  const events = useRef<NotificationEvents>({
    revision: 0,
    records: new Map(),
    handoffs: new Map(),
    terminals: new Map(),
    acknowledgedIds: new Set(),
    revisions: new Map(),
    typeRevisions: new Map(),
    children: new Map(),
    waiting: new Set(),
    held: new Map()
  });
  const [notifications, setNotifications] = useState<UnifiedNotification[]>(() => {
    // Restore notifications from localStorage on mount
    const restoredNotifications: UnifiedNotification[] = [];
    const mountedAt = new Date();
    const persistentKeys = Object.values(NOTIFICATION_STORAGE_KEYS);

    for (const key of persistentKeys) {
      try {
        // One key can hold several cards: a type that owns one card per entity persists them
        // together, and every running one is restored.
        for (const parsed of readPersistedCards(key)) {
          if (parsed.type === 'game_detection' && parsed.details?.parentOperationId) {
            if (parsed.details.operationId)
              events.current.children.set(
                parsed.details.operationId,
                parsed.details.parentOperationId
              );
            clearPersistedNotificationIfTargeted(
              key,
              { operationId: parsed.details.operationId },
              parsed.id
            );
            continue;
          }
          if (parsed.status === 'running') {
            // Strip cancel-intent flags: they are live-session UI state. A persisted
            // cancelRequested (X clicked before the operationId arrived) would re-arm the
            // deferred-cancel watchdog after reload, and the NEXT operation of this type to
            // land an operationId in the slot gets cancelled at birth - seen as corruption
            // removals / cache clears / cache size scans dying instantly after registration.
            const details = parsed.details ? { ...parsed.details } : undefined;
            if (details) {
              for (const key of LIVE_ONLY_CANCEL_DETAIL_KEYS) {
                delete details[key];
              }
            }
            restoredNotifications.push({
              ...parsed,
              details,
              startedAt: Number.isFinite(new Date(parsed.startedAt).getTime())
                ? new Date(parsed.startedAt)
                : mountedAt
            });
          }
        }
      } catch {
        // Invalid JSON, skip
      }
    }

    return restoredNotifications;
  });

  const notificationsRef = useRef(notifications);
  notificationsRef.current = notifications;
  const recoveryRef = useRef<(() => Promise<void>) | null>(null);
  const requestRecovery = useCallback(() => {
    void recoveryRef.current?.();
  }, []);
  const signalR = useSignalR();
  const { authMode, isLoading: authLoading } = useAuth();
  const isAdmin = authMode === 'authenticated';

  // Timer management for auto-dismiss
  const autoDismissTimersRef = useRef<
    Map<string, { timerId: ReturnType<typeof setTimeout>; instanceId: number }>
  >(new Map());
  const instanceCounterRef = useRef<Map<string, number>>(new Map());

  // Track when the tab was hidden, so we can debounce visibility recovery
  const tabHiddenAtRef = useRef<number | null>(null);

  const getNextInstanceId = useCallback((notificationId: string): number => {
    const current = instanceCounterRef.current.get(notificationId) || 0;
    const next = current + 1;
    instanceCounterRef.current.set(notificationId, next);
    return next;
  }, []);

  const cancelAutoDismissTimer = useCallback((notificationId: string) => {
    const existing = autoDismissTimersRef.current.get(notificationId);
    if (existing) {
      clearTimeout(existing.timerId);
      autoDismissTimersRef.current.delete(notificationId);
    }
  }, []);

  const addNotification = useCallback(
    (notification: Omit<UnifiedNotification, 'id' | 'startedAt'>): string => {
      let id = '';

      // Map notification types to their singleton IDs
      const typeToIdMap: Record<string, string> = {
        log_processing: NOTIFICATION_IDS.LOG_PROCESSING,
        cache_clearing: NOTIFICATION_IDS.CACHE_CLEARING,
        database_reset: NOTIFICATION_IDS.DATABASE_RESET,
        depot_mapping: NOTIFICATION_IDS.DEPOT_MAPPING,
        log_removal: NOTIFICATION_IDS.LOG_REMOVAL,
        game_removal: NOTIFICATION_IDS.GAME_REMOVAL,
        service_removal: NOTIFICATION_IDS.SERVICE_REMOVAL,
        corruption_removal: NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        game_detection: NOTIFICATION_IDS.GAME_DETECTION,
        corruption_detection: NOTIFICATION_IDS.CORRUPTION_DETECTION,
        data_import: NOTIFICATION_IDS.DATA_IMPORT,
        epic_game_mapping: NOTIFICATION_IDS.EPIC_GAME_MAPPING,
        xbox_game_mapping: NOTIFICATION_IDS.XBOX_GAME_MAPPING,
        battle_net_game_mapping: NOTIFICATION_IDS.BATTLE_NET_GAME_MAPPING,
        riot_game_mapping: NOTIFICATION_IDS.RIOT_GAME_MAPPING,
        eviction_scan: NOTIFICATION_IDS.EVICTION_SCAN,
        eviction_removal: NOTIFICATION_IDS.EVICTION_REMOVAL,
        scheduled_prefill: NOTIFICATION_IDS.SCHEDULED_PREFILL
      };

      if (typeToIdMap[notification.type]) {
        id = typeToIdMap[notification.type];
      } else if (notification.type === 'generic' && notification.message) {
        // For generic notifications, create a deterministic ID based on message
        // This prevents duplicate notifications with the same message
        const messageHash = notification.message.replace(/[^a-zA-Z0-9]/g, '_').substring(0, 50);
        id = `generic_${messageHash}`;
      } else {
        id = `notification_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
      }

      const newNotification: UnifiedNotification = {
        ...notification,
        id,
        startedAt: new Date()
      };

      const operationId = notification.details?.operationId;
      const exact = operationId
        ? notificationsRef.current.find(
            (n) => n.type === notification.type && n.details?.operationId === operationId
          )
        : undefined;
      if (exact) id = exact.id;
      if (notification.type === 'game_detection' && notification.details?.parentOperationId)
        return id;
      if (
        operationId &&
        events.current.terminals.has(operationId) &&
        !isTerminalNotificationStatus(notification.status)
      )
        return id;
      setNotifications((prev) => {
        const existing =
          (operationId
            ? prev.find(
                (n) => n.type === notification.type && n.details?.operationId === operationId
              )
            : undefined) ?? prev.find((n) => n.id === id);
        const sameRun = !!operationId && existing?.details?.operationId === operationId;
        if (sameRun && existing && isTerminalNotificationStatus(existing.status)) return prev;
        if (
          existing &&
          !sameRun &&
          operationId &&
          existing.details?.operationId &&
          !isTerminalNotificationStatus(existing.status)
        )
          return prev;
        if (
          operationId &&
          events.current.terminals.has(operationId) &&
          !isTerminalNotificationStatus(notification.status)
        )
          return prev;
        const card: UnifiedNotification =
          sameRun && existing
            ? {
                ...existing,
                ...notification,
                id: existing.id,
                startedAt: existing.startedAt,
                instanceVersion: existing.instanceVersion,
                progress: existing.progress ?? notification.progress,
                progressMode: existing.progressMode ?? notification.progressMode,
                detailMessage: existing.detailMessage ?? notification.detailMessage,
                progressAriaValueText:
                  existing.progressAriaValueText ?? notification.progressAriaValueText,
                message: existing.message,
                details: {
                  ...existing.details,
                  ...Object.fromEntries(
                    Object.entries(notification.details ?? {}).filter(
                      ([, value]) => value !== undefined
                    )
                  )
                }
              }
            : { ...newNotification, id, instanceVersion: getNextInstanceId(id) };
        if (card.details?.cancelRequested && !isTerminalNotificationStatus(card.status))
          card.status = 'cancelling';
        if (!sameRun) cancelAutoDismissTimer(card.id);
        const entry = NOTIFICATION_REGISTRY.find((candidate) => candidate.type === card.type);
        if (entry) persistNotification(entry.storageKey, card, entry.getId !== undefined);
        if (isTerminalNotificationStatus(card.status)) scheduleAutoDismiss(card.id);
        return existing ? prev.map((n) => (n === existing ? card : n)) : [...prev, card];
      });

      return id;
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    []
  );

  const updateNotification = useCallback(
    (
      id: string,
      updates:
        | Partial<UnifiedNotification>
        | ((notification: UnifiedNotification) => Partial<UnifiedNotification>)
    ) => {
      setNotifications((prev: UnifiedNotification[]) =>
        prev.map((n) =>
          n.id === id
            ? (() => {
                const patch = typeof updates === 'function' ? updates(n) : updates;
                if (
                  isTerminalNotificationStatus(n.status) &&
                  patch.status &&
                  !isTerminalNotificationStatus(patch.status)
                )
                  return n;
                return {
                  ...n,
                  ...patch,
                  id: n.id,
                  startedAt: n.startedAt,
                  instanceVersion: n.instanceVersion
                };
              })()
            : n
        )
      );
    },
    []
  );

  const removeNotificationAnimated = useCallback(
    (id: string, expected: UnifiedNotification, instanceId: number) => {
      if (autoDismissTimersRef.current.get(id)?.instanceId !== instanceId) return;
      window.dispatchEvent(
        new CustomEvent(APP_EVENTS.NOTIFICATION_REMOVING, { detail: { notificationId: id } })
      );
      const timerId = setTimeout(() => {
        if (autoDismissTimersRef.current.get(id)?.instanceId !== instanceId) return;
        autoDismissTimersRef.current.delete(id);
        setNotifications((prev) =>
          prev.filter((n) => {
            if (
              n.id !== id ||
              n.instanceVersion !== expected.instanceVersion ||
              n.details?.operationId !== expected.details?.operationId ||
              n.startedAt.getTime() !== expected.startedAt.getTime() ||
              !isTerminalNotificationStatus(n.status)
            )
              return true;
            const terminal = n.details?.operationId
              ? events.current.terminals.get(n.details.operationId)
              : undefined;
            if (terminal) terminal.presented = true;
            return false;
          })
        );
      }, NOTIFICATION_ANIMATION_DURATION_MS);
      autoDismissTimersRef.current.set(id, { timerId, instanceId });
    },
    []
  );

  /**
   * Schedule auto-dismiss for a notification.
   *
   * Safe to call from anywhere - the timer callback checks notification state
   * when it fires (after delayMs), by which time React state is committed.
   * Also, createStartedHandler cancels any existing timer when a new operation
   * starts with the same ID, preventing race conditions.
   *
   * @param notificationId - ID of the notification to auto-dismiss
   * @param delayMs - Delay before dismissing (default: AUTO_DISMISS_DELAY_MS)
   */
  const scheduleAutoDismiss = useCallback(
    (notificationId: string, delayMs: number = AUTO_DISMISS_DELAY_MS) => {
      if (!shouldAutoDismiss()) return;

      cancelAutoDismissTimer(notificationId);
      const instanceId = getNextInstanceId(notificationId);

      const timerId = setTimeout(() => {
        const currentTimer = autoDismissTimersRef.current.get(notificationId);
        if (currentTimer && currentTimer.instanceId === instanceId) {
          setNotifications((prev: UnifiedNotification[]) => {
            const notification = prev.find((n) => n.id === notificationId);
            // Only dismiss if notification exists and is in a terminal state
            if (notification && isTerminalNotificationStatus(notification.status)) {
              // Defer to avoid setState-during-render (CustomEvent triggers UniversalNotificationBar setState)
              queueMicrotask(() =>
                removeNotificationAnimated(notificationId, notification, instanceId)
              );
            }
            return prev;
          });
        }
      }, delayMs);

      autoDismissTimersRef.current.set(notificationId, { timerId, instanceId });
    },
    [cancelAutoDismissTimer, getNextInstanceId, removeNotificationAnimated]
  );

  const removeNotification = useCallback(
    (id: string) => {
      cancelAutoDismissTimer(id);
      setNotifications((prev: UnifiedNotification[]) =>
        prev.filter((n) => {
          if (n.id !== id) return true;
          const terminal = n.details?.operationId
            ? events.current.terminals.get(n.details.operationId)
            : undefined;
          if (terminal) terminal.presented = true;
          return false;
        })
      );
    },
    [cancelAutoDismissTimer]
  );

  const clearCompletedNotifications = useCallback(() => {
    setNotifications((prev: UnifiedNotification[]) => {
      const terminal = prev.filter((n) => isTerminalNotificationStatus(n.status));
      terminal.forEach((n) => {
        cancelAutoDismissTimer(n.id);
        const outcome = n.details?.operationId
          ? events.current.terminals.get(n.details.operationId)
          : undefined;
        if (outcome) outcome.presented = true;
      });
      return prev.filter((n) => !isTerminalNotificationStatus(n.status));
    });
  }, [cancelAutoDismissTimer]);

  // Registry-driven handlers for standard notification lifecycle types
  useNotificationHandlers(
    NOTIFICATION_REGISTRY,
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer,
    events,
    requestRecovery
  );

  // Toast notifications
  React.useEffect(() => {
    const handleShowToast = (e: Event) => {
      const event = e as CustomEvent<ShowToastEvent>;
      const { message, type, duration } = event.detail;

      const notificationId = addNotification({
        type: 'generic',
        status: 'completed',
        message,
        details: {
          notificationType: type
        }
      });

      scheduleAutoDismiss(notificationId, duration ?? TOAST_DEFAULT_DURATION_MS);
    };

    window.addEventListener(APP_EVENTS.SHOW_TOAST, handleShowToast);
    return () => window.removeEventListener(APP_EVENTS.SHOW_TOAST, handleShowToast);
  }, [addNotification, scheduleAutoDismiss]);

  // Listen for "Keep Notifications Visible" preference changes
  // When turned off, schedule auto-dismiss for ALL completed/failed notifications
  React.useEffect(() => {
    const handleNotificationVisibilityChange = () => {
      // Check if "Keep Notifications Visible" was just turned OFF
      if (shouldAutoDismiss()) {
        // Collect notification IDs to schedule, then schedule OUTSIDE setNotifications
        const idsToSchedule: string[] = [];

        setNotifications((prev: UnifiedNotification[]) => {
          prev.forEach((n) => {
            if (isTerminalNotificationStatus(n.status)) {
              idsToSchedule.push(n.id);
            }
          });
          return prev;
        });

        // CRITICAL: Schedule auto-dismiss OUTSIDE setNotifications callback
        idsToSchedule.forEach((id) => scheduleAutoDismiss(id));
      }
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
  }, [scheduleAutoDismiss]);

  // Authenticated fetch helper for recovery operations - fires from a SignalR-reconnect effect with
  // no user action involved (a network blip or laptop wake can reconnect while the tab is genuinely
  // idle), so it carries the same activity signal as ApiService.getFetchOptions() rather than keeping
  // LastSeenAtUtc artificially fresh on every reconnect.
  const fetchWithAuth: FetchWithAuth = useCallback(async (url: string): Promise<Response> => {
    return fetch(url, {
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json',
        'X-User-Active': hasRecentUserInteraction(120_000) ? 'true' : 'false'
      }
    });
  }, []);

  const recoverAllOperations = useMemo(
    () =>
      createRecoveryRunner(
        fetchWithAuth,
        setNotifications,
        scheduleAutoDismiss,
        events,
        () => notificationsRef.current,
        cancelAutoDismissTimer
      ),
    [fetchWithAuth, scheduleAutoDismiss, cancelAutoDismissTimer]
  );
  recoveryRef.current = recoverAllOperations;

  React.useEffect(() => {
    for (const notification of notifications) {
      if (isTerminalNotificationStatus(notification.status) && notification.details?.operationId) {
        const terminal = events.current.terminals.get(notification.details.operationId);
        if (terminal) terminal.presented = true;
        else
          events.current.terminals.set(notification.details.operationId, {
            operationId: notification.details.operationId,
            status: notification.status as 'completed' | 'failed' | 'cancelled' | 'skipped',
            error: notification.error,
            presented: true
          });
      }
    }
  }, [notifications]);

  React.useEffect(
    () => () => {
      for (const { timerId } of autoDismissTimersRef.current.values()) clearTimeout(timerId);
      autoDismissTimersRef.current.clear();
    },
    []
  );

  // Recovery on page load (admin-only - all recovery endpoints require admin access)
  React.useEffect(() => {
    if (authLoading || !isAdmin) return;

    recoverAllOperations();
  }, [authLoading, isAdmin, recoverAllOperations]);

  // Re-run recovery on every connect, including the first one: the page-load recovery above can
  // seed a running card before the hub subscription is live, so a completion emitted in that gap
  // would otherwise leave the card running forever.
  useReconnectRefetch(signalR.isConnected, () => {
    // Skip if not admin - all recovery endpoints require admin access
    if (authLoading || !isAdmin) return;

    recoverAllOperations();
  });

  // Recovery on tab becoming visible after being backgrounded.
  // The SignalR connection stays open while backgrounded, so the reconnection effect
  // never fires - but the browser may have throttled/dropped message processing.
  // This effect detects the tab returning to the foreground and re-runs recovery
  // if the tab was hidden for more than 2 seconds.
  React.useEffect(() => {
    const MIN_HIDDEN_MS = 2000;

    const handleVisibilityChange = () => {
      if (document.visibilityState === 'hidden') {
        tabHiddenAtRef.current = Date.now();
        return;
      }

      // Tab became visible
      if (!isAdmin || authLoading) return;

      const hiddenAt = tabHiddenAtRef.current;
      tabHiddenAtRef.current = null;

      if (hiddenAt !== null && Date.now() - hiddenAt >= MIN_HIDDEN_MS) {
        recoverAllOperations();
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, [isAdmin, authLoading, recoverAllOperations]);

  // Compute if any removal operation is running (these all share a backend lock)
  const isAnyRemovalRunning = useMemo(
    () =>
      notifications.some(
        (n) =>
          REMOVAL_TYPES.includes(n.type as (typeof REMOVAL_TYPES)[number]) && n.status === 'running'
      ),
    [notifications]
  );

  const activeRemovalType = useMemo(
    () =>
      notifications.find(
        (n) =>
          REMOVAL_TYPES.includes(n.type as (typeof REMOVAL_TYPES)[number]) && n.status === 'running'
      )?.type ?? null,
    [notifications]
  );

  const value = {
    events,
    notifications,
    addNotification,
    updateNotification,
    removeNotification,
    clearCompletedNotifications,
    isAnyRemovalRunning,
    activeRemovalType,
    scheduleAutoDismiss
  };

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
};
