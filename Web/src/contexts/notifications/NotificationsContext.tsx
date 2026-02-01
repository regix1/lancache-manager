import React, { createContext, useContext, useState, useCallback, useRef, useMemo, type ReactNode } from 'react';
import { useSignalR } from '../SignalRContext';
import { useAuth } from '../AuthContext';
import themeService from '@services/theme.service';
import type {
  ProcessingProgressEvent,
  FastProcessingCompleteEvent,
  LogRemovalProgressEvent,
  LogRemovalCompleteEvent,
  GameRemovalProgressEvent,
  GameRemovalCompleteEvent,
  ServiceRemovalProgressEvent,
  ServiceRemovalCompleteEvent,
  CorruptionRemovalStartedEvent,
  CorruptionRemovalProgressEvent,
  CorruptionRemovalCompleteEvent,
  CorruptionDetectionStartedEvent,
  CorruptionDetectionProgressEvent,
  CorruptionDetectionCompleteEvent,
  GameDetectionStartedEvent,
  GameDetectionProgressEvent,
  GameDetectionCompleteEvent,
  DatabaseResetProgressEvent,
  CacheClearProgressEvent,
  CacheClearCompleteEvent,
  DepotMappingStartedEvent,
  DepotMappingProgressEvent,
  DepotMappingCompleteEvent,
  SteamSessionErrorEvent,
  ShowToastEvent
} from '../SignalRContext/types';

import type { UnifiedNotification, NotificationsContextType } from './types';
import {
  AUTO_DISMISS_DELAY_MS,
  CANCELLED_NOTIFICATION_DELAY_MS,
  NOTIFICATION_ANIMATION_DURATION_MS,
  STEAM_ERROR_DISMISS_DELAY_MS,
  TOAST_DEFAULT_DURATION_MS,
  INCREMENTAL_SCAN_ANIMATION_STEPS,
  INCREMENTAL_SCAN_ANIMATION_DURATION_MS,
  NOTIFICATION_STORAGE_KEYS,
  NOTIFICATION_IDS
} from './constants';
import {
  createStartedHandler,
  createProgressHandler,
  createCompletionHandler,
  createStatusAwareProgressHandler
} from './handlerFactories';
import {
  createSimpleRecoveryFunction,
  createCacheRemovalsRecoveryFunction,
  RECOVERY_CONFIGS
} from './recoveryFactory';
import {
  formatLogProcessingMessage,
  formatLogProcessingDetailMessage,
  formatLogProcessingCompletionMessage,
  formatFastProcessingCompletionMessage,
  formatDepotMappingDetailMessage,
  formatLogRemovalProgressMessage,
  formatLogRemovalCompleteMessage,
  formatGameRemovalProgressMessage,
  formatServiceRemovalProgressMessage,
  formatCorruptionRemovalStartedMessage,
  formatCorruptionRemovalCompleteMessage,
  formatGameDetectionStartedMessage,
  formatGameDetectionProgressMessage,
  formatGameDetectionCompleteMessage,
  formatGameDetectionFailureMessage,
  formatCorruptionDetectionStartedMessage,
  formatCorruptionDetectionProgressMessage,
  formatCorruptionDetectionCompleteMessage,
  formatCorruptionDetectionFailureMessage,
  formatDatabaseResetProgressMessage,
  formatDatabaseResetCompleteMessage,
  formatCacheClearProgressMessage,
  formatCacheClearCompleteMessage,
  formatCacheClearFailureMessage,
  formatDepotMappingStartedMessage,
  formatDepotMappingProgressMessage
} from './detailMessageFormatters';

const NotificationsContext = createContext<NotificationsContextType | undefined>(undefined);

export const useNotifications = () => {
  const context = useContext(NotificationsContext);
  if (!context) {
    throw new Error('useNotifications must be used within NotificationsProvider');
  }
  return context;
};

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
  const { isAuthenticated, isLoading: authLoading } = useAuth();

  // Timer management for auto-dismiss
  const autoDismissTimersRef = useRef<Map<string, { timerId: ReturnType<typeof setTimeout>; instanceId: number }>>(new Map());
  const instanceCounterRef = useRef<Map<string, number>>(new Map());

  // Track previous SignalR connection state to detect reconnections
  const prevConnectionStateRef = useRef<string | null>(null);

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

  const addNotification = useCallback((notification: Omit<UnifiedNotification, 'id' | 'startedAt'>): string => {
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
      corruption_detection: NOTIFICATION_IDS.CORRUPTION_DETECTION
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
  }, []);

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
            if (notification && (notification.status === 'completed' || notification.status === 'failed')) {
              autoDismissTimersRef.current.delete(notificationId);
              removeNotificationAnimated(notificationId);
            }
            return prev;
          });
        }
      }, delayMs);

      autoDismissTimersRef.current.set(notificationId, { timerId, instanceId });
    },
    [cancelAutoDismissTimer, getNextInstanceId, removeNotificationAnimated]
  );

  const removeNotification = useCallback((id: string) => {
    cancelAutoDismissTimer(id);
    setNotifications((prev: UnifiedNotification[]) => prev.filter((n) => n.id !== id));
  }, [cancelAutoDismissTimer]);

  const clearCompletedNotifications = useCallback(() => {
    setNotifications((prev: UnifiedNotification[]) => {
      const completed = prev.filter((n) => n.status === 'completed' || n.status === 'failed');
      completed.forEach((n) => cancelAutoDismissTimer(n.id));
      return prev.filter((n) => n.status === 'running');
    });
  }, [cancelAutoDismissTimer]);

  // SignalR Event Handlers
  React.useEffect(() => {
    // ========== Log Processing ==========
    const handleProcessingProgress = (event: ProcessingProgressEvent) => {
      const notificationId = NOTIFICATION_IDS.LOG_PROCESSING;
      const status = event.status || 'processing';

      // Handle completion - replace all log_processing notifications with completed state
      if (status.toLowerCase() === 'completed') {
        localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING);
        setNotifications((prev: UnifiedNotification[]) => [
          ...prev.filter((n) => n.type !== 'log_processing'),
          {
            id: notificationId,
            type: 'log_processing' as const,
            status: 'completed' as const,
            message: 'Processing Complete!',
            detailMessage: formatLogProcessingCompletionMessage(event.entriesProcessed),
            progress: 100,
            startedAt: new Date()
          }
        ]);
        scheduleAutoDismiss(notificationId);
        return;
      }

      // Handle progress updates
      const progress = Math.min(99.9, event.percentComplete || event.progress || 0);
      const message = formatLogProcessingMessage(event);
      const detailMessage = formatLogProcessingDetailMessage(event);
      const details = {
        operationId: event.operationId,
        mbProcessed: event.mbProcessed,
        mbTotal: event.mbTotal,
        entriesProcessed: event.entriesProcessed,
        totalLines: event.totalLines
      };

      setNotifications((prev: UnifiedNotification[]) => {
        // Skip if notification is already in a terminal state
        if (prev.some((n) => n.id === notificationId && (n.status === 'completed' || n.status === 'failed'))) {
          return prev;
        }

        const existingRunning = prev.find((n) => n.id === notificationId && n.status === 'running');
        if (existingRunning) {
          return prev.map((n) =>
            n.id === notificationId
              ? { ...n, message, detailMessage, progress, details: { ...n.details, ...details } }
              : n
          );
        }

        // Create new notification
        cancelAutoDismissTimer(notificationId);
        const newNotification: UnifiedNotification = {
          id: notificationId,
          type: 'log_processing',
          status: 'running',
          message,
          detailMessage,
          progress,
          startedAt: new Date(),
          details
        };
        localStorage.setItem(NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING, JSON.stringify(newNotification));
        return [...prev.filter((n) => n.type !== 'log_processing'), newNotification];
      });
    };

    const handleFastProcessingComplete = (result: FastProcessingCompleteEvent) => {
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.LOG_PROCESSING);
      const fixedNotificationId = NOTIFICATION_IDS.LOG_PROCESSING;

      setNotifications((prev: UnifiedNotification[]) => {
        const filtered = prev.filter((n) => n.type !== 'log_processing');
        return [
          ...filtered,
          {
            id: fixedNotificationId,
            type: 'log_processing' as const,
            status: 'completed' as const,
            message: 'Processing Complete!',
            detailMessage: formatFastProcessingCompletionMessage(result.entriesProcessed, result.linesProcessed, result.elapsed),
            progress: 100,
            startedAt: new Date()
          }
        ];
      });

      scheduleAutoDismiss(fixedNotificationId);
    };

    // ========== Log Removal (using status-aware factory for proper completion handling) ==========
    const handleLogRemovalProgress = createStatusAwareProgressHandler<LogRemovalProgressEvent>(
      {
        type: 'log_removal',
        getId: () => NOTIFICATION_IDS.LOG_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
        getMessage: formatLogRemovalProgressMessage,
        getProgress: (e) => e.percentComplete || 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : e.status === 'error' ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Log removal completed',
        getErrorMessage: (e) => e.message || 'Log removal failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleLogRemovalComplete = createCompletionHandler<LogRemovalCompleteEvent>(
      {
        type: 'log_removal',
        getId: () => NOTIFICATION_IDS.LOG_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
        getSuccessMessage: formatLogRemovalCompleteMessage,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          linesProcessed: e.linesProcessed
        }),
        useAnimationDelay: true
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Game Removal (using factory) ==========
    const handleGameRemovalProgress = createStatusAwareProgressHandler<GameRemovalProgressEvent>(
      {
        type: 'game_removal',
        getId: () => NOTIFICATION_IDS.GAME_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        getMessage: formatGameRemovalProgressMessage,
        getProgress: () => 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : e.status === 'error' ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Game removal completed',
        getErrorMessage: (e) => e.message || 'Game removal failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleGameRemovalComplete = createCompletionHandler<GameRemovalCompleteEvent>(
      {
        type: 'game_removal',
        getId: () => NOTIFICATION_IDS.GAME_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed,
          logEntriesRemoved: e.logEntriesRemoved
        })
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Service Removal (using factory) ==========
    const handleServiceRemovalProgress = createStatusAwareProgressHandler<ServiceRemovalProgressEvent>(
      {
        type: 'service_removal',
        getId: () => NOTIFICATION_IDS.SERVICE_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
        getMessage: formatServiceRemovalProgressMessage,
        getProgress: () => 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : e.status === 'error' ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Service removal completed',
        getErrorMessage: (e) => e.message || 'Service removal failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleServiceRemovalComplete = createCompletionHandler<ServiceRemovalCompleteEvent>(
      {
        type: 'service_removal',
        getId: () => NOTIFICATION_IDS.SERVICE_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed,
          logEntriesRemoved: e.logEntriesRemoved
        })
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Corruption Removal (using factory) ==========
    const handleCorruptionRemovalStarted = createStartedHandler<CorruptionRemovalStartedEvent>(
      {
        type: 'corruption_removal',
        getId: () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        defaultMessage: 'Removing corrupted chunks...',
        getMessage: formatCorruptionRemovalStartedMessage,
        getDetails: (e) => ({ operationId: e.operationId, service: e.service })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleCorruptionRemovalProgress = createStatusAwareProgressHandler<CorruptionRemovalProgressEvent>(
      {
        type: 'corruption_removal',
        getId: () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        getMessage: (e) => e.message || `Removing corrupted chunks: ${e.status}`,
        getProgress: (e) => e.percentComplete ?? 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : (e.status === 'failed' || e.status === 'cancelled') ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Corruption removal completed',
        getErrorMessage: (e) => e.message || 'Corruption removal failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleCorruptionRemovalComplete = createCompletionHandler<CorruptionRemovalCompleteEvent>(
      {
        type: 'corruption_removal',
        getId: () => NOTIFICATION_IDS.CORRUPTION_REMOVAL,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        getSuccessMessage: formatCorruptionRemovalCompleteMessage,
        useAnimationDelay: true
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Game Detection (using factory) ==========
    const handleGameDetectionStarted = createStartedHandler<GameDetectionStartedEvent>(
      {
        type: 'game_detection',
        getId: () => NOTIFICATION_IDS.GAME_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
        defaultMessage: 'Detecting games and services...',
        getMessage: formatGameDetectionStartedMessage,
        getDetails: (e) => ({ operationId: e.operationId, scanType: e.scanType })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleGameDetectionProgress = createStatusAwareProgressHandler<GameDetectionProgressEvent>(
      {
        type: 'game_detection',
        getId: () => NOTIFICATION_IDS.GAME_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
        getMessage: formatGameDetectionProgressMessage,
        getProgress: (e) => e.progressPercent || 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : (e.status === 'failed' || e.status === 'cancelled') ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Game detection completed',
        getErrorMessage: (e) => e.message || 'Game detection failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleGameDetectionComplete = createCompletionHandler<GameDetectionCompleteEvent>(
      {
        type: 'game_detection',
        getId: () => NOTIFICATION_IDS.GAME_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
        getSuccessMessage: formatGameDetectionCompleteMessage,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          totalGamesDetected: e.totalGamesDetected,
          totalServicesDetected: e.totalServicesDetected
        }),
        getFailureMessage: formatGameDetectionFailureMessage,
        supportFastCompletion: true,
        getFastCompletionId: () => NOTIFICATION_IDS.GAME_DETECTION
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Corruption Detection (using factory) ==========
    const handleCorruptionDetectionStarted = createStartedHandler<CorruptionDetectionStartedEvent>(
      {
        type: 'corruption_detection',
        getId: () => NOTIFICATION_IDS.CORRUPTION_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
        defaultMessage: 'Scanning for corrupted cache chunks...',
        getMessage: formatCorruptionDetectionStartedMessage,
        getDetails: (e) => ({ operationId: e.operationId })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleCorruptionDetectionProgress = createStatusAwareProgressHandler<CorruptionDetectionProgressEvent>(
      {
        type: 'corruption_detection',
        getId: () => NOTIFICATION_IDS.CORRUPTION_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
        getMessage: formatCorruptionDetectionProgressMessage,
        getProgress: (e) => e.percentComplete || 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : (e.status === 'failed' || e.status === 'cancelled') ? 'failed' : undefined,
        getCompletedMessage: (e) => e.message || 'Corruption detection completed',
        getErrorMessage: (e) => e.message || 'Corruption detection failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    const handleCorruptionDetectionComplete = createCompletionHandler<CorruptionDetectionCompleteEvent>(
      {
        type: 'corruption_detection',
        getId: () => NOTIFICATION_IDS.CORRUPTION_DETECTION,
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
        getSuccessMessage: formatCorruptionDetectionCompleteMessage,
        getFailureMessage: formatCorruptionDetectionFailureMessage,
        supportFastCompletion: true,
        getFastCompletionId: () => NOTIFICATION_IDS.CORRUPTION_DETECTION
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // ========== Database Reset ==========
    const handleDatabaseResetProgress = createStatusAwareProgressHandler<DatabaseResetProgressEvent>(
      {
        type: 'database_reset',
        getId: () => NOTIFICATION_IDS.DATABASE_RESET,
        storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
        getMessage: formatDatabaseResetProgressMessage,
        getProgress: (e) => e.percentComplete || 0,
        getStatus: (e) => e.status,
        getCompletedMessage: formatDatabaseResetCompleteMessage,
        getErrorMessage: (e) => e.message
      },
      setNotifications,
      scheduleAutoDismiss,  // Use direct scheduling - we know notification is in terminal state
      cancelAutoDismissTimer
    );

    // ========== Cache Clearing (using status-aware factory for proper completion handling) ==========
    const cacheClearHandler = createStatusAwareProgressHandler<CacheClearProgressEvent>(
      {
        type: 'cache_clearing',
        getId: () => NOTIFICATION_IDS.CACHE_CLEARING,
        storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
        getMessage: formatCacheClearProgressMessage,
        getProgress: (e) => e.percentComplete || 0,
        getStatus: (e) => e.status === 'complete' ? 'completed' : (e.status === 'failed' || e.status === 'cancelled') ? 'failed' : undefined,
        getCompletedMessage: (e) => e.statusMessage || 'Cache cleared successfully',
        getErrorMessage: (e) => e.error || e.statusMessage || 'Cache clear failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    // Wrap with logging to debug stuck notifications
    const handleCacheClearProgress = (event: CacheClearProgressEvent) => {
      const derivedStatus = event.status === 'complete' ? 'completed' : (event.status === 'failed' || event.status === 'cancelled') ? 'failed' : undefined;
      console.log('[CacheClear] Progress event received:', {
        rawStatus: event.status,
        derivedStatus,
        statusMessage: event.statusMessage,
        error: event.error,
        percentComplete: event.percentComplete,
        operationId: event.operationId
      });
      cacheClearHandler(event);
    };

    const cacheClearCompleteHandler = createCompletionHandler<CacheClearCompleteEvent>(
      {
        type: 'cache_clearing',
        getId: () => NOTIFICATION_IDS.CACHE_CLEARING,
        storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
        getSuccessMessage: formatCacheClearCompleteMessage,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          directoriesProcessed: e.directoriesProcessed
        }),
        getFailureMessage: formatCacheClearFailureMessage
      },
      setNotifications,
      scheduleAutoDismiss  // Use direct scheduling - we know notification is in terminal state
    );

    // Wrap with logging to debug stuck notifications
    const handleCacheClearComplete = (event: CacheClearCompleteEvent) => {
      console.log('[CacheClear] Complete event received:', {
        success: event.success,
        message: event.message,
        error: event.error,
        filesDeleted: event.filesDeleted,
        directoriesProcessed: event.directoriesProcessed
      });
      cacheClearCompleteHandler(event);
    };

    // ========== Depot Mapping (progress/complete handlers kept inline due to complexity) ==========
    const handleDepotMappingStartedBase = createStartedHandler<DepotMappingStartedEvent>(
      {
        type: 'depot_mapping',
        getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
        storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
        defaultMessage: 'Starting depot mapping scan...',
        getMessage: formatDepotMappingStartedMessage,
        getDetails: (e) => ({ operationId: e.operationId, isLoggedOn: e.isLoggedOn }),
        replaceExisting: true // Depot mapping can be restarted
      },
      setNotifications,
      cancelAutoDismissTimer
    );
    const handleDepotMappingStarted = (event: DepotMappingStartedEvent) => {
      handleDepotMappingStartedBase(event);
    };

    // ========== Depot Mapping Progress Handler ==========
    const handleDepotMappingProgress = createStatusAwareProgressHandler<DepotMappingProgressEvent>(
      {
        type: 'depot_mapping',
        getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
        storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
        getMessage: (event) => formatDepotMappingProgressMessage(event, undefined),
        getProgress: (event) => event.percentComplete ?? event.progressPercent ?? 0,
        getStatus: (e) => {
          if (e.status === 'complete' || e.status === 'Complete') return 'completed';
          if (e.status === 'error' || e.status === 'failed' || e.status === 'Error') return 'failed';
          return undefined;
        },
        getCompletedMessage: (e) => e.message || 'Depot mapping completed successfully',
        getErrorMessage: (e) => e.message || 'Depot mapping failed'
      },
      setNotifications,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    // ========== Depot Mapping Completion Helpers ==========

    /** Animates progress from current value to 100% over multiple steps */
    const animateProgressToCompletion = (
      notificationId: string,
      startProgress: number,
      successMessage: string,
      successDetails: Record<string, unknown>,
      onComplete: () => void
    ) => {
      const steps = INCREMENTAL_SCAN_ANIMATION_STEPS;
      const interval = INCREMENTAL_SCAN_ANIMATION_DURATION_MS / steps;
      const progressIncrement = (100 - startProgress) / steps;
      let currentStep = 0;

      const animationInterval = setInterval(() => {
        currentStep++;
        const newProgress = Math.min(100, startProgress + progressIncrement * currentStep);

        setNotifications((prev: UnifiedNotification[]) =>
          prev.map((n) =>
            n.id === notificationId
              ? { ...n, progress: newProgress, message: newProgress >= 100 ? successMessage : n.message }
              : n
          )
        );

        if (currentStep >= steps) {
          clearInterval(animationInterval);
          setTimeout(() => {
            setNotifications((prev: UnifiedNotification[]) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (!existing) return prev;

              localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);
              return prev.map((n) =>
                n.id === notificationId
                  ? { ...n, status: 'completed' as const, message: successMessage, details: { ...n.details, ...successDetails } }
                  : n
              );
            });
            onComplete();
          }, NOTIFICATION_ANIMATION_DURATION_MS);
        }
      }, interval);
    };

    /** Handles depot mapping cancellation */
    const handleDepotMappingCancelled = (notificationId: string) => {
      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);

      // Capture the startedAt we're about to set for the timer
      const newStartedAt = new Date();

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - create notification for cancellation
          const newNotification: UnifiedNotification = {
            id: notificationId,
            type: 'depot_mapping',
            status: 'completed',
            message: 'Depot mapping scan cancelled',
            startedAt: newStartedAt,
            progress: 100,
            details: { cancelled: true }
          };
          return [...prev, newNotification];
        }

        // Update existing notification - keep the original startedAt
        return prev.map((n) =>
          n.id === notificationId
            ? { ...n, status: 'completed' as const, message: 'Depot mapping scan cancelled', progress: 100, details: { ...n.details, cancelled: true } }
            : n
        );
      });

      // Schedule auto-dismiss for the cancelled notification
      scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
    };

    /** Handles successful depot mapping completion */
    const handleDepotMappingSuccess = (event: DepotMappingCompleteEvent, notificationId: string) => {
      const successMessage = event.message || 'Depot mapping completed successfully';
      const successDetails = { totalMappings: event.totalMappings, downloadsUpdated: event.downloadsUpdated };
      const isIncremental = event.scanMode === 'incremental';

      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);

      if (isIncremental) {
        // For incremental scans, animate progress to 100%
        setNotifications((prev: UnifiedNotification[]) => {
          const notification = prev.find((n) => n.id === notificationId);

          if (!notification) {
            // Fast completion - create completed notification
            const newNotification: UnifiedNotification = {
              id: notificationId,
              type: 'depot_mapping',
              status: 'completed',
              message: successMessage,
              startedAt: new Date(),
              progress: 100,
              details: successDetails
            };
            return [...prev, newNotification];
          }

          // Animation callback will schedule auto-dismiss when complete
          animateProgressToCompletion(
            notificationId,
            notification.progress || 0,
            successMessage,
            successDetails,
            () => scheduleAutoDismiss(notificationId)  // Use direct scheduling
          );
          return prev;
        });

        // Schedule auto-dismiss for fast completion case (animation handles the existing case)
        scheduleAutoDismiss(notificationId);  // Use direct scheduling
      } else {
        // For full scans, complete immediately
        setNotifications((prev: UnifiedNotification[]) => {
          const existing = prev.find((n) => n.id === notificationId);

          if (!existing) {
            // Fast completion - create completed notification
            const newNotification: UnifiedNotification = {
              id: notificationId,
              type: 'depot_mapping',
              status: 'completed',
              message: successMessage,
              startedAt: new Date(),
              progress: 100,
              details: successDetails
            };
            return [...prev, newNotification];
          }

          // Update existing notification
          return prev.map((n) =>
            n.id === notificationId
              ? { ...n, status: 'completed' as const, message: successMessage, progress: 100, details: { ...n.details, ...successDetails } }
              : n
          );
        });

        // Schedule auto-dismiss AFTER state update - use direct scheduling
        scheduleAutoDismiss(notificationId);
      }
    };

    /** Handles failed depot mapping with optional full scan modal trigger */
    const handleDepotMappingFailure = (event: DepotMappingCompleteEvent, notificationId: string) => {
      const errorMessage = event.error || event.message || 'Depot mapping failed';
      const requiresFullScan =
        errorMessage.includes('change gap is too large') ||
        errorMessage.includes('requires full scan') ||
        errorMessage.includes('requires a full scan');

      if (requiresFullScan) {
        window.dispatchEvent(new CustomEvent('show-full-scan-modal', { detail: { error: errorMessage } }));
      }

      localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);

      setNotifications((prev: UnifiedNotification[]) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Fast completion - create failed notification
          const newNotification: UnifiedNotification = {
            id: notificationId,
            type: 'depot_mapping',
            status: 'failed',
            message: 'Depot mapping failed',
            error: errorMessage,
            startedAt: new Date(),
            progress: 100
          };
          return [...prev, newNotification];
        }

        // Update existing notification
        return prev.map((n) =>
          n.id === notificationId
            ? { ...n, status: 'failed' as const, error: errorMessage, progress: 100 }
            : n
        );
      });

      // Schedule auto-dismiss AFTER state update - use direct scheduling
      scheduleAutoDismiss(notificationId);
    };

    /** Main depot mapping completion dispatcher */
    const handleDepotMappingComplete = (event: DepotMappingCompleteEvent) => {
      const notificationId = NOTIFICATION_IDS.DEPOT_MAPPING;

      if (event.cancelled) {
        handleDepotMappingCancelled(notificationId);
      } else if (event.success) {
        handleDepotMappingSuccess(event, notificationId);
      } else {
        handleDepotMappingFailure(event, notificationId);
      }
    };

    // ========== Steam Session Error ==========
    const handleSteamSessionError = (event: SteamSessionErrorEvent) => {
      const getTitle = () => {
        switch (event.errorType) {
          case 'SessionReplaced':
          case 'LoggedInElsewhere':
            return 'Steam Session Replaced';
          case 'AutoLogout':
            return 'Steam Auto-Logout';
          case 'InvalidCredentials':
          case 'AuthenticationRequired':
          case 'SessionExpired':
            return 'Steam Authentication Required';
          case 'ServerUnavailable':
          case 'ServiceUnavailable':
            return 'Steam Service Unavailable';
          case 'RateLimited':
            return 'Steam Rate Limited';
          default:
            return 'Steam Error';
        }
      };

      const id = addNotification({
        type: 'generic',
        status: 'failed',
        message: getTitle(),
        detailMessage: event.message || 'An error occurred with the Steam session',
        details: {
          notificationType: 'error'
        }
      });

      // Schedule auto-dismiss for the error notification
      scheduleAutoDismiss(id, STEAM_ERROR_DISMISS_DELAY_MS);
    };

    // Subscribe to events
    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('FastProcessingComplete', handleFastProcessingComplete);
    signalR.on('LogRemovalProgress', handleLogRemovalProgress);
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('GameRemovalProgress', handleGameRemovalProgress);
    signalR.on('GameRemovalComplete', handleGameRemovalComplete);
    signalR.on('ServiceRemovalProgress', handleServiceRemovalProgress);
    signalR.on('ServiceRemovalComplete', handleServiceRemovalComplete);
    signalR.on('CorruptionRemovalStarted', handleCorruptionRemovalStarted);
    signalR.on('CorruptionRemovalProgress', handleCorruptionRemovalProgress);
    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    signalR.on('GameDetectionStarted', handleGameDetectionStarted);
    signalR.on('GameDetectionProgress', handleGameDetectionProgress);
    signalR.on('GameDetectionComplete', handleGameDetectionComplete);
    signalR.on('CorruptionDetectionStarted', handleCorruptionDetectionStarted);
    signalR.on('CorruptionDetectionProgress', handleCorruptionDetectionProgress);
    signalR.on('CorruptionDetectionComplete', handleCorruptionDetectionComplete);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('CacheClearProgress', handleCacheClearProgress);
    signalR.on('CacheClearComplete', handleCacheClearComplete);
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);
    signalR.on('SteamSessionError', handleSteamSessionError);

    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('FastProcessingComplete', handleFastProcessingComplete);
      signalR.off('LogRemovalProgress', handleLogRemovalProgress);
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('GameRemovalProgress', handleGameRemovalProgress);
      signalR.off('GameRemovalComplete', handleGameRemovalComplete);
      signalR.off('ServiceRemovalProgress', handleServiceRemovalProgress);
      signalR.off('ServiceRemovalComplete', handleServiceRemovalComplete);
      signalR.off('CorruptionRemovalStarted', handleCorruptionRemovalStarted);
      signalR.off('CorruptionRemovalProgress', handleCorruptionRemovalProgress);
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
      signalR.off('GameDetectionStarted', handleGameDetectionStarted);
      signalR.off('GameDetectionProgress', handleGameDetectionProgress);
      signalR.off('GameDetectionComplete', handleGameDetectionComplete);
      signalR.off('CorruptionDetectionStarted', handleCorruptionDetectionStarted);
      signalR.off('CorruptionDetectionProgress', handleCorruptionDetectionProgress);
      signalR.off('CorruptionDetectionComplete', handleCorruptionDetectionComplete);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('CacheClearProgress', handleCacheClearProgress);
      signalR.off('CacheClearComplete', handleCacheClearComplete);
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
      signalR.off('SteamSessionError', handleSteamSessionError);
    };
  }, [signalR, addNotification, updateNotification, scheduleAutoDismiss, scheduleAutoDismiss, cancelAutoDismissTimer]);

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
    return () => window.removeEventListener('notificationvisibilitychange', handleNotificationVisibilityChange);
  }, [scheduleAutoDismiss]);

  // Recovery on page load
  React.useEffect(() => {
    if (authLoading || !isAuthenticated) return;

    const fetchWithAuth = async (url: string): Promise<Response> => {
      return fetch(url, {
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json'
        }
      });
    };

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

    const recoverCacheRemovals = createCacheRemovalsRecoveryFunction(
      fetchWithAuth,
      setNotifications,
      scheduleAutoDismiss
    );

    const recoverAllOperations = async () => {
      try {
        await Promise.allSettled([
          recoverLogProcessing(),
          recoverLogRemoval(),
          recoverDepotMapping(),
          recoverCacheClearing(),
          recoverDatabaseReset(),
          recoverGameDetection(),
          recoverCorruptionDetection(),
          recoverCacheRemovals()
        ]);
      } catch (err) {
        console.error('[NotificationsContext] Failed to recover operations:', err);
      }
    };

    recoverAllOperations();
  }, [authLoading, isAuthenticated, scheduleAutoDismiss]);

  // Monitor SignalR connection state - re-run recovery on reconnection
  // This ensures we recover from missed completion events during connection loss (especially on mobile)
  React.useEffect(() => {
    // Skip if not authenticated
    if (authLoading || !isAuthenticated) return;

    const currentState = signalR.connectionState;
    const prevState = prevConnectionStateRef.current;

    // Update the ref for next comparison
    prevConnectionStateRef.current = currentState;

    // Only trigger recovery on RECONNECTION (not initial connection)
    // Reconnection is when we transition TO 'connected' FROM 'reconnecting' or 'disconnected'
    // but NOT from null (initial mount) since the auth-based recovery handles that
    if (
      currentState === 'connected' &&
      prevState !== null &&
      prevState !== 'connected'
    ) {
      const fetchWithAuth = async (url: string): Promise<Response> => {
        return fetch(url, {
          credentials: 'include',
          headers: {
            'Content-Type': 'application/json'
          }
        });
      };

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

      const recoverCacheRemovals = createCacheRemovalsRecoveryFunction(
        fetchWithAuth,
        setNotifications,
        scheduleAutoDismiss
      );

      const recoverAllOperations = async () => {
        try {
          await Promise.allSettled([
            recoverLogProcessing(),
            recoverLogRemoval(),
            recoverDepotMapping(),
            recoverCacheClearing(),
            recoverDatabaseReset(),
            recoverGameDetection(),
            recoverCorruptionDetection(),
            recoverCacheRemovals()
          ]);
        } catch (err) {
          console.error('[NotificationsContext] Failed to recover operations after reconnection:', err);
        }
      };

      recoverAllOperations();
    }
  }, [signalR.connectionState, authLoading, isAuthenticated, scheduleAutoDismiss]);

  // Removal/clearing operation types that share the backend _cacheLock
  const REMOVAL_TYPES = ['log_removal', 'game_removal', 'service_removal', 'corruption_removal', 'cache_clearing'] as const;

  // Compute if any removal operation is running (these all share a backend lock)
  const isAnyRemovalRunning = useMemo(
    () => notifications.some(n => REMOVAL_TYPES.includes(n.type as typeof REMOVAL_TYPES[number]) && n.status === 'running'),
    [notifications]
  );

  const activeRemovalType = useMemo(
    () => notifications.find(n => REMOVAL_TYPES.includes(n.type as typeof REMOVAL_TYPES[number]) && n.status === 'running')?.type ?? null,
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
