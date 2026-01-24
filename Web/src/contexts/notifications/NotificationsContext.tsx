import React, { createContext, useContext, useState, useCallback, useRef, type ReactNode } from 'react';
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
  CorruptionRemovalCompleteEvent,
  CorruptionDetectionStartedEvent,
  CorruptionDetectionCompleteEvent,
  GameDetectionStartedEvent,
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
  createDynamicRecoveryFunction,
  createCacheRemovalsRecoveryFunction,
  RECOVERY_CONFIGS
} from './recoveryFactory';
import {
  formatLogProcessingMessage,
  formatLogProcessingDetailMessage,
  formatLogProcessingCompletionMessage,
  formatFastProcessingCompletionMessage,
  formatDepotMappingDetailMessage
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
 * Returns false if:
 * - disableStickyNotifications is false (sticky mode ON)
 * - OR picsAlwaysVisible is true (keep all notifications visible)
 */
const shouldAutoDismiss = (): boolean => {
  // If "Keep Notifications Visible" is enabled, never auto-dismiss
  if (themeService.getPicsAlwaysVisibleSync()) {
    return false;
  }
  // Otherwise, respect the sticky notifications setting
  return !themeService.getDisableStickyNotificationsSync();
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

    if (notification.type === 'log_processing') {
      id = NOTIFICATION_IDS.LOG_PROCESSING;
    } else if (notification.type === 'cache_clearing') {
      id = NOTIFICATION_IDS.CACHE_CLEARING;
    } else if (notification.type === 'database_reset') {
      id = NOTIFICATION_IDS.DATABASE_RESET;
    } else if (notification.type === 'depot_mapping') {
      id = NOTIFICATION_IDS.DEPOT_MAPPING;
    } else if (notification.type === 'log_removal' && notification.details?.service) {
      id = NOTIFICATION_IDS.logRemoval(notification.details.service);
    } else if (notification.type === 'game_removal' && notification.details?.gameAppId !== undefined) {
      id = NOTIFICATION_IDS.gameRemoval(notification.details.gameAppId);
    } else if (notification.type === 'service_removal' && notification.details?.service) {
      id = NOTIFICATION_IDS.serviceRemoval(notification.details.service);
    } else if (notification.type === 'corruption_removal' && notification.details?.service) {
      id = NOTIFICATION_IDS.corruptionRemoval(notification.details.service);
    } else if (notification.type === 'game_detection' && notification.details?.operationId) {
      id = NOTIFICATION_IDS.gameDetection(notification.details.operationId);
    } else if (notification.type === 'corruption_detection' && notification.details?.operationId) {
      id = NOTIFICATION_IDS.corruptionDetection(notification.details.operationId);
    } else {
      id = `notification_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
    }

    const newNotification: UnifiedNotification = {
      ...notification,
      id,
      startedAt: new Date()
    };

    setNotifications((prev) => {
      const filtered = prev.filter((n) => n.id !== id);
      return [...filtered, newNotification];
    });

    if (notification.status !== 'running') {
      scheduleAutoDismiss(id);
    }

    return id;
  }, []);

  const updateNotification = useCallback((id: string, updates: Partial<UnifiedNotification>) => {
    setNotifications((prev) =>
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
      setNotifications((prev) => prev.filter((n) => n.id !== id));
    }, NOTIFICATION_ANIMATION_DURATION_MS);
  }, []);

  const scheduleAutoDismiss = useCallback(
    (notificationId: string, delayMs: number = AUTO_DISMISS_DELAY_MS) => {
      if (shouldAutoDismiss()) {
        cancelAutoDismissTimer(notificationId);

        const instanceId = getNextInstanceId(notificationId);

        const timerId = setTimeout(() => {
          const currentTimer = autoDismissTimersRef.current.get(notificationId);
          if (currentTimer && currentTimer.instanceId === instanceId) {
            setNotifications((prev) => {
              const notification = prev.find((n) => n.id === notificationId);
              if (notification && (notification.status === 'completed' || notification.status === 'failed')) {
                autoDismissTimersRef.current.delete(notificationId);
                removeNotificationAnimated(notificationId);
              }
              return prev;
            });
          }
        }, delayMs);

        autoDismissTimersRef.current.set(notificationId, { timerId, instanceId });
      }
    },
    [cancelAutoDismissTimer, getNextInstanceId, removeNotificationAnimated]
  );

  const removeNotification = useCallback((id: string) => {
    cancelAutoDismissTimer(id);
    setNotifications((prev) => prev.filter((n) => n.id !== id));
  }, [cancelAutoDismissTimer]);

  const clearCompletedNotifications = useCallback(() => {
    setNotifications((prev) => {
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
        setNotifications((prev) => [
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
        mbProcessed: event.mbProcessed,
        mbTotal: event.mbTotal,
        entriesProcessed: event.entriesProcessed,
        totalLines: event.totalLines
      };

      setNotifications((prev) => {
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

      setNotifications((prev) => {
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

    // ========== Log Removal (using factory) ==========
    const handleLogRemovalProgress = createProgressHandler<LogRemovalProgressEvent>(
      {
        type: 'log_removal',
        getId: (e) => NOTIFICATION_IDS.logRemoval(e.service),
        storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
        getMessage: (e) => {
          const linesRemoved = e.linesRemoved || 0;
          return linesRemoved > 0
            ? `Removing ${e.service} entries (${linesRemoved.toLocaleString()} removed)...`
            : e.message || `Removing ${e.service} entries...`;
        },
        getProgress: (e) => e.percentComplete || 0,
        getDetails: (e) => ({
          service: e.service,
          linesProcessed: e.linesProcessed,
          linesRemoved: e.linesRemoved
        })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleLogRemovalComplete = createCompletionHandler<LogRemovalCompleteEvent>(
      {
        type: 'log_removal',
        getId: (e) => NOTIFICATION_IDS.logRemoval(e.service),
        storageKey: NOTIFICATION_STORAGE_KEYS.LOG_REMOVAL,
        getSuccessMessage: (e) => e.message || `Successfully removed ${e.service} entries`,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          linesProcessed: e.linesProcessed
        }),
        useAnimationDelay: true
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Game Removal (using factory) ==========
    const handleGameRemovalProgress = createProgressHandler<GameRemovalProgressEvent>(
      {
        type: 'game_removal',
        getId: (e) => NOTIFICATION_IDS.gameRemoval(e.gameAppId),
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        getMessage: (e) => e.message || `Removing ${e.gameName}...`,
        getDetails: (e) => ({
          gameAppId: e.gameAppId,
          gameName: e.gameName,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed
        })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleGameRemovalComplete = createCompletionHandler<GameRemovalCompleteEvent>(
      {
        type: 'game_removal',
        getId: (e) => NOTIFICATION_IDS.gameRemoval(e.gameAppId),
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_REMOVAL,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed,
          logEntriesRemoved: e.logEntriesRemoved
        })
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Service Removal (using factory) ==========
    const handleServiceRemovalProgress = createProgressHandler<ServiceRemovalProgressEvent>(
      {
        type: 'service_removal',
        getId: (e) => NOTIFICATION_IDS.serviceRemoval(e.serviceName),
        storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
        getMessage: (e) => e.message || `Removing ${e.serviceName} cache...`,
        getDetails: (e) => ({
          service: e.serviceName,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed
        })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleServiceRemovalComplete = createCompletionHandler<ServiceRemovalCompleteEvent>(
      {
        type: 'service_removal',
        getId: (e) => NOTIFICATION_IDS.serviceRemoval(e.serviceName),
        storageKey: NOTIFICATION_STORAGE_KEYS.SERVICE_REMOVAL,
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          bytesFreed: e.bytesFreed,
          logEntriesRemoved: e.logEntriesRemoved
        })
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Corruption Removal (using factory) ==========
    const handleCorruptionRemovalStarted = createStartedHandler<CorruptionRemovalStartedEvent>(
      {
        type: 'corruption_removal',
        getId: (e) => NOTIFICATION_IDS.corruptionRemoval(e.service),
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        defaultMessage: 'Removing corrupted chunks...',
        getMessage: (e) => e.message || `Removing corrupted chunks for ${e.service}...`,
        getDetails: (e) => ({ operationId: e.operationId })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleCorruptionRemovalComplete = createCompletionHandler<CorruptionRemovalCompleteEvent>(
      {
        type: 'corruption_removal',
        getId: (e) => NOTIFICATION_IDS.corruptionRemoval(e.service),
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_REMOVAL,
        getSuccessMessage: (e) => e.message || `Successfully removed corrupted chunks for ${e.service}`,
        useAnimationDelay: true
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Game Detection (using factory) ==========
    const handleGameDetectionStarted = createStartedHandler<GameDetectionStartedEvent>(
      {
        type: 'game_detection',
        getId: (e) => NOTIFICATION_IDS.gameDetection(e.operationId),
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
        defaultMessage: 'Detecting games and services...',
        getMessage: (e) => e.message || 'Detecting games and services in cache...',
        getDetails: (e) => ({ operationId: e.operationId, scanType: e.scanType })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleGameDetectionComplete = createCompletionHandler<GameDetectionCompleteEvent>(
      {
        type: 'game_detection',
        getId: (e) => NOTIFICATION_IDS.gameDetection(e.operationId),
        storageKey: NOTIFICATION_STORAGE_KEYS.GAME_DETECTION,
        getSuccessMessage: (e) => e.message || 'Game detection completed',
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          totalGamesDetected: e.totalGamesDetected,
          totalServicesDetected: e.totalServicesDetected
        }),
        getFailureMessage: (e) => e.message || 'Game detection failed',
        supportFastCompletion: true,
        getFastCompletionId: () => NOTIFICATION_IDS.gameDetection(String(Date.now()))
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Corruption Detection (using factory) ==========
    const handleCorruptionDetectionStarted = createStartedHandler<CorruptionDetectionStartedEvent>(
      {
        type: 'corruption_detection',
        getId: (e) => NOTIFICATION_IDS.corruptionDetection(e.operationId),
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
        defaultMessage: 'Scanning for corrupted cache chunks...',
        getMessage: (e) => e.message || 'Scanning for corrupted cache chunks...',
        getDetails: (e) => ({ operationId: e.operationId })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleCorruptionDetectionComplete = createCompletionHandler<CorruptionDetectionCompleteEvent>(
      {
        type: 'corruption_detection',
        getId: (e) => NOTIFICATION_IDS.corruptionDetection(e.operationId),
        storageKey: NOTIFICATION_STORAGE_KEYS.CORRUPTION_DETECTION,
        getSuccessMessage: (e) => e.message || 'Corruption scan completed',
        getFailureMessage: (e) => e.message || 'Corruption scan failed',
        supportFastCompletion: true,
        getFastCompletionId: () => NOTIFICATION_IDS.corruptionDetection(String(Date.now()))
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Database Reset ==========
    const handleDatabaseResetProgress = createStatusAwareProgressHandler<DatabaseResetProgressEvent>(
      {
        type: 'database_reset',
        getId: () => NOTIFICATION_IDS.DATABASE_RESET,
        storageKey: NOTIFICATION_STORAGE_KEYS.DATABASE_RESET,
        getMessage: (e) => e.message || 'Resetting database...',
        getProgress: (e) => e.percentComplete || 0,
        getStatus: (e) => e.status,
        getCompletedMessage: (e) => e.message || 'Database reset completed',
        getErrorMessage: (e) => e.message
      },
      setNotifications,
      updateNotification,
      scheduleAutoDismiss,
      cancelAutoDismissTimer
    );

    // ========== Cache Clearing ==========
    const handleCacheClearProgress = createProgressHandler<CacheClearProgressEvent>(
      {
        type: 'cache_clearing',
        getId: () => NOTIFICATION_IDS.CACHE_CLEARING,
        storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
        getMessage: (e) => e.statusMessage || 'Clearing cache...',
        getProgress: (e) => e.percentComplete || 0,
        getDetails: (e, existing) => ({
          filesDeleted: e.filesDeleted || 0,
          directoriesProcessed: e.directoriesProcessed || 0,
          bytesDeleted: e.bytesDeleted || 0,
          operationId: e.operationId || existing?.details?.operationId
        })
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    const handleCacheClearComplete = createCompletionHandler<CacheClearCompleteEvent>(
      {
        type: 'cache_clearing',
        getId: () => NOTIFICATION_IDS.CACHE_CLEARING,
        storageKey: NOTIFICATION_STORAGE_KEYS.CACHE_CLEARING,
        getSuccessMessage: (e) => e.message || 'Cache cleared successfully',
        getSuccessDetails: (e, existing) => ({
          ...existing?.details,
          filesDeleted: e.filesDeleted,
          directoriesProcessed: e.directoriesProcessed
        }),
        getFailureMessage: (e) => e.error || e.message || 'Cache clear failed'
      },
      setNotifications,
      scheduleAutoDismiss
    );

    // ========== Depot Mapping (progress/complete handlers kept inline due to complexity) ==========
    const handleDepotMappingStarted = createStartedHandler<DepotMappingStartedEvent>(
      {
        type: 'depot_mapping',
        getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
        storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
        defaultMessage: 'Starting depot mapping scan...',
        getMessage: (e) => e.message || 'Starting depot mapping scan...',
        getDetails: (e) => ({ isLoggedOn: e.isLoggedOn }),
        replaceExisting: true // Depot mapping can be restarted
      },
      setNotifications,
      cancelAutoDismissTimer
    );

    // ========== Depot Mapping Progress Handler ==========
    const handleDepotMappingProgress = createProgressHandler<DepotMappingProgressEvent>(
      {
        type: 'depot_mapping',
        getId: () => NOTIFICATION_IDS.DEPOT_MAPPING,
        storageKey: NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING,
        getMessage: (event, existing) => event.message || existing?.message || 'Scanning depot mappings...',
        getDetailMessage: (event) => formatDepotMappingDetailMessage(event),
        getProgress: (event) => event.percentComplete ?? 0,
        getDetails: (event, existing) => ({
          totalMappings: event.totalMappings,
          processedMappings: event.processedMappings,
          mappingsApplied: event.mappingsApplied,
          percentComplete: event.percentComplete,
          isLoggedOn: event.isLoggedOn ?? existing?.details?.isLoggedOn
        })
      },
      setNotifications,
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

        setNotifications((prev) =>
          prev.map((n) =>
            n.id === notificationId
              ? { ...n, progress: newProgress, message: newProgress >= 100 ? successMessage : n.message }
              : n
          )
        );

        if (currentStep >= steps) {
          clearInterval(animationInterval);
          setTimeout(() => {
            setNotifications((prev) => {
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
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) return prev;

        localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);
        return prev.map((n) =>
          n.id === notificationId
            ? { ...n, status: 'completed' as const, message: 'Depot mapping scan cancelled', progress: 100, details: { ...n.details, cancelled: true } }
            : n
        );
      });
      scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
    };

    /** Handles successful depot mapping completion */
    const handleDepotMappingSuccess = (event: DepotMappingCompleteEvent, notificationId: string) => {
      const successMessage = event.message || 'Depot mapping completed successfully';
      const successDetails = { totalMappings: event.totalMappings, downloadsUpdated: event.downloadsUpdated };
      const isIncremental = event.scanMode === 'incremental';

      if (isIncremental) {
        // For incremental scans, animate progress to 100%
        setNotifications((prev) => {
          const notification = prev.find((n) => n.id === notificationId);
          if (!notification) return prev;

          animateProgressToCompletion(
            notificationId,
            notification.progress || 0,
            successMessage,
            successDetails,
            () => scheduleAutoDismiss(notificationId)
          );
          return prev;
        });
      } else {
        // For full scans, complete immediately
        setNotifications((prev) => {
          const existing = prev.find((n) => n.id === notificationId);
          if (!existing) return prev;

          localStorage.removeItem(NOTIFICATION_STORAGE_KEYS.DEPOT_MAPPING);
          return prev.map((n) =>
            n.id === notificationId
              ? { ...n, status: 'completed' as const, message: successMessage, details: { ...n.details, ...successDetails } }
              : n
          );
        });
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
      updateNotification(notificationId, { status: 'failed', error: errorMessage });
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

      setTimeout(() => {
        scheduleAutoDismiss(id, STEAM_ERROR_DISMISS_DELAY_MS);
      }, 100);
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
    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    signalR.on('GameDetectionStarted', handleGameDetectionStarted);
    signalR.on('GameDetectionComplete', handleGameDetectionComplete);
    signalR.on('CorruptionDetectionStarted', handleCorruptionDetectionStarted);
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
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
      signalR.off('GameDetectionStarted', handleGameDetectionStarted);
      signalR.off('GameDetectionComplete', handleGameDetectionComplete);
      signalR.off('CorruptionDetectionStarted', handleCorruptionDetectionStarted);
      signalR.off('CorruptionDetectionComplete', handleCorruptionDetectionComplete);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('CacheClearProgress', handleCacheClearProgress);
      signalR.off('CacheClearComplete', handleCacheClearComplete);
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
      signalR.off('SteamSessionError', handleSteamSessionError);
    };
  }, [signalR, addNotification, updateNotification, scheduleAutoDismiss, cancelAutoDismissTimer]);

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
        // Find ALL completed/failed notifications and schedule auto-dismiss
        setNotifications((prev) => {
          prev.forEach((n) => {
            if (n.status === 'completed' || n.status === 'failed') {
              scheduleAutoDismiss(n.id);
            }
          });
          return prev;
        });
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

    const recoverLogRemoval = createDynamicRecoveryFunction(
      RECOVERY_CONFIGS.logRemoval,
      fetchWithAuth,
      setNotifications,
      scheduleAutoDismiss
    );

    const recoverGameDetection = createDynamicRecoveryFunction(
      RECOVERY_CONFIGS.gameDetection,
      fetchWithAuth,
      setNotifications,
      scheduleAutoDismiss
    );

    const recoverCorruptionDetection = createDynamicRecoveryFunction(
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

  const value = {
    notifications,
    addNotification,
    updateNotification,
    removeNotification,
    clearCompletedNotifications
  };

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
};
