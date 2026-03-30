import React, { useState, useCallback, useRef, useMemo, type ReactNode } from 'react';
import { useSignalR } from '../SignalRContext/useSignalR';
import { useAuth } from '../useAuth';
import themeService from '@services/theme.service';
import type { ShowToastEvent } from '../SignalRContext/types';

import type { UnifiedNotification } from './types';
import {
  AUTO_DISMISS_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  TOAST_DEFAULT_DURATION_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS
} from './constants';
import { createRecoveryRunner, type FetchWithAuth } from './recoveryFactory';
import { NOTIFICATION_REGISTRY } from './notificationRegistry';
import { useNotificationHandlers } from './useNotificationHandlers';
import { createSpecialCaseHandlers } from './specialCaseHandlers';

import { NotificationsContext } from './NotificationsContext.types';

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

// Removal/clearing operation types that share the backend _cacheLock
const REMOVAL_TYPES = [
  'log_removal',
  'game_removal',
  'service_removal',
  'corruption_removal',
  'cache_clearing'
] as const;

export const NotificationsProvider: React.FC<NotificationsProviderProps> = ({ children }) => {
  const [notifications, setNotifications] = useState<UnifiedNotification[]>(() => {
    // Restore notifications from localStorage on mount
    const restoredNotifications: UnifiedNotification[] = [];
    const persistentKeys = Object.values(NOTIFICATION_STORAGE_KEYS);

    for (const key of persistentKeys) {
      try {
        const saved = localStorage.getItem(key);
        if (saved) {
          const parsed = JSON.parse(saved) as UnifiedNotification;
          if (parsed.status === 'running') {
            restoredNotifications.push({
              ...parsed,
              startedAt: new Date(parsed.startedAt)
            });
          }
        }
      } catch {
        // Invalid JSON, skip
      }
    }

    return restoredNotifications;
  });

  const signalR = useSignalR();
  const { authMode, isLoading: authLoading } = useAuth();
  const isAdmin = authMode === 'authenticated';

  // Timer management for auto-dismiss
  const autoDismissTimersRef = useRef<
    Map<string, { timerId: ReturnType<typeof setTimeout>; instanceId: number }>
  >(new Map());
  const instanceCounterRef = useRef<Map<string, number>>(new Map());

  // Track previous SignalR connection state to detect reconnections
  const prevConnectionStateRef = useRef<string | null>(null);

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
        eviction_scan: NOTIFICATION_IDS.EVICTION_SCAN,
        eviction_removal: NOTIFICATION_IDS.EVICTION_REMOVAL
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

      setNotifications((prev: UnifiedNotification[]) => {
        const filtered = prev.filter((n) => n.id !== id);
        return [...filtered, newNotification];
      });

      if (notification.status !== 'running') {
        scheduleAutoDismiss(id);
      }

      return id;
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    []
  );

  const updateNotification = useCallback((id: string, updates: Partial<UnifiedNotification>) => {
    setNotifications((prev: UnifiedNotification[]) =>
      prev.map((n) => (n.id === id ? { ...n, ...updates } : n))
    );
  }, []);

  const removeNotificationAnimated = useCallback((id: string) => {
    window.dispatchEvent(
      new CustomEvent('notification-removing', {
        detail: { notificationId: id }
      })
    );

    setTimeout(() => {
      setNotifications((prev: UnifiedNotification[]) => prev.filter((n) => n.id !== id));
    }, NOTIFICATION_ANIMATION_DURATION_MS);
  }, []);

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
            if (
              notification &&
              (notification.status === 'completed' || notification.status === 'failed')
            ) {
              autoDismissTimersRef.current.delete(notificationId);
              // Defer to avoid setState-during-render (CustomEvent triggers UniversalNotificationBar setState)
              queueMicrotask(() => removeNotificationAnimated(notificationId));
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
      setNotifications((prev: UnifiedNotification[]) => prev.filter((n) => n.id !== id));
    },
    [cancelAutoDismissTimer]
  );

  const clearCompletedNotifications = useCallback(() => {
    setNotifications((prev: UnifiedNotification[]) => {
      const completed = prev.filter((n) => n.status === 'completed' || n.status === 'failed');
      completed.forEach((n) => cancelAutoDismissTimer(n.id));
      return prev.filter((n) => n.status === 'running');
    });
  }, [cancelAutoDismissTimer]);

  // Registry-driven handlers (handles 11 standard notification lifecycle types)
  useNotificationHandlers(
    NOTIFICATION_REGISTRY,
    setNotifications,
    scheduleAutoDismiss,
    cancelAutoDismissTimer,
    removeNotification
  );

  // Special case handlers that don't fit the standard Started->Progress->Complete registry pattern:
  // - Depot Mapping: uses special createDepotMappingCompletionHandler
  // - Database Reset: only started + progress, no complete event
  // - Epic Game Mapping: progress only + custom EpicGameMappingsUpdated one-shot handler
  // - Steam Session Error: custom one-shot error display
  React.useEffect(() => {
    const handlers = createSpecialCaseHandlers(
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    signalR.on('DepotMappingStarted', handlers.handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handlers.handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handlers.handleDepotMappingComplete);
    signalR.on('DatabaseResetStarted', handlers.handleDatabaseResetStarted);
    signalR.on('DatabaseResetProgress', handlers.handleDatabaseResetProgress);
    signalR.on('EpicMappingProgress', handlers.handleEpicMappingProgress);
    signalR.on('EpicGameMappingsUpdated', handlers.handleEpicGameMappingsUpdated);
    signalR.on('SteamSessionError', handlers.handleSteamSessionError);

    return () => {
      signalR.off('DepotMappingStarted', handlers.handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handlers.handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handlers.handleDepotMappingComplete);
      signalR.off('DatabaseResetStarted', handlers.handleDatabaseResetStarted);
      signalR.off('DatabaseResetProgress', handlers.handleDatabaseResetProgress);
      signalR.off('EpicMappingProgress', handlers.handleEpicMappingProgress);
      signalR.off('EpicGameMappingsUpdated', handlers.handleEpicGameMappingsUpdated);
      signalR.off('SteamSessionError', handlers.handleSteamSessionError);
    };
  }, [signalR, scheduleAutoDismiss, cancelAutoDismissTimer]);

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
          notificationType: type || 'info'
        }
      });

      if (shouldAutoDismiss()) {
        setTimeout(() => {
          removeNotificationAnimated(notificationId);
        }, duration || TOAST_DEFAULT_DURATION_MS);
      }
    };

    window.addEventListener('show-toast', handleShowToast);
    return () => window.removeEventListener('show-toast', handleShowToast);
  }, [addNotification, removeNotificationAnimated]);

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
            if (n.status === 'completed' || n.status === 'failed') {
              idsToSchedule.push(n.id);
            }
          });
          return prev;
        });

        // CRITICAL: Schedule auto-dismiss OUTSIDE setNotifications callback
        idsToSchedule.forEach((id) => scheduleAutoDismiss(id));
      }
    };

    window.addEventListener('notificationvisibilitychange', handleNotificationVisibilityChange);
    return () =>
      window.removeEventListener(
        'notificationvisibilitychange',
        handleNotificationVisibilityChange
      );
  }, [scheduleAutoDismiss]);

  // Authenticated fetch helper for recovery operations
  const fetchWithAuth: FetchWithAuth = useCallback(async (url: string): Promise<Response> => {
    return fetch(url, {
      credentials: 'include',
      headers: {
        'Content-Type': 'application/json'
      }
    });
  }, []);

  // Recovery on page load (admin-only — all recovery endpoints require admin access)
  React.useEffect(() => {
    if (authLoading || !isAdmin) return;

    const recoverAllOperations = createRecoveryRunner(
      fetchWithAuth,
      setNotifications,
      scheduleAutoDismiss
    );

    recoverAllOperations();
  }, [authLoading, isAdmin, fetchWithAuth, scheduleAutoDismiss]);

  // Monitor SignalR connection state - re-run recovery on reconnection
  // This ensures we recover from missed completion events during connection loss (especially on mobile)
  React.useEffect(() => {
    // Skip if not admin — all recovery endpoints require admin access
    if (authLoading || !isAdmin) return;

    const currentState = signalR.connectionState;
    const prevState = prevConnectionStateRef.current;

    // Update the ref for next comparison
    prevConnectionStateRef.current = currentState;

    // Only trigger recovery on RECONNECTION (not initial connection)
    // Reconnection is when we transition TO 'connected' FROM 'reconnecting' or 'disconnected'
    // but NOT from null (initial mount) since the auth-based recovery handles that
    if (currentState === 'connected' && prevState !== null && prevState !== 'connected') {
      const recoverAllOperations = createRecoveryRunner(
        fetchWithAuth,
        setNotifications,
        scheduleAutoDismiss
      );

      recoverAllOperations();
    }
  }, [signalR.connectionState, authLoading, isAdmin, fetchWithAuth, scheduleAutoDismiss]);

  // Recovery on tab becoming visible after being backgrounded.
  // The SignalR connection stays open while backgrounded, so the reconnection effect
  // never fires — but the browser may have throttled/dropped message processing.
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
        const recoverAllOperations = createRecoveryRunner(
          fetchWithAuth,
          setNotifications,
          scheduleAutoDismiss
        );
        recoverAllOperations();
      }
    };

    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, [isAdmin, authLoading, fetchWithAuth, scheduleAutoDismiss]);

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
    notifications,
    addNotification,
    updateNotification,
    removeNotification,
    clearCompletedNotifications,
    isAnyRemovalRunning,
    activeRemovalType
  };

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
};
