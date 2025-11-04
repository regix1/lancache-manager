import React, { createContext, useContext, useState, useCallback, type ReactNode } from 'react';
import { useSignalR } from './SignalRContext';
import themeService from '@services/theme.service';

// Notification timing constants
const AUTO_DISMISS_DELAY_MS = 5000; // Standard auto-dismiss delay for completed/failed notifications
const CANCELLED_NOTIFICATION_DELAY_MS = 3000; // Shorter delay for cancelled operations
const NOTIFICATION_ANIMATION_DURATION_MS = 300; // Animation duration for notification removal
const INCREMENTAL_SCAN_ANIMATION_DURATION_MS = 1500; // Progress animation for incremental scans
const INCREMENTAL_SCAN_ANIMATION_STEPS = 30; // Number of steps in incremental scan animation

// Unified notification type for all background operations
export type NotificationType =
  | 'log_processing'
  | 'cache_clearing'
  | 'service_removal'
  | 'game_removal'
  | 'database_reset'
  | 'depot_mapping'
  | 'generic';

export type NotificationStatus = 'running' | 'completed' | 'failed';

export interface UnifiedNotification {
  id: string;
  type: NotificationType;
  status: NotificationStatus;
  progress?: number;
  message: string;
  detailMessage?: string;
  startedAt: Date;

  // Type-specific details (optional, based on type)
  details?: {
    // For log_processing
    mbProcessed?: number;
    mbTotal?: number;
    entriesProcessed?: number;
    totalLines?: number;
    estimatedTime?: string;

    // For cache_clearing
    filesDeleted?: number;
    directoriesProcessed?: number;
    bytesDeleted?: number;

    // For service_removal
    service?: string;
    linesProcessed?: number;
    linesRemoved?: number;

    // For game_removal
    gameAppId?: number;
    gameName?: string;
    bytesFreed?: number;
    logEntriesRemoved?: number;

    // For depot_mapping
    totalMappings?: number;
    processedMappings?: number;
    mappingsApplied?: number;
    percentComplete?: number;
    isProcessing?: boolean;
    isLoggedOn?: boolean;
    downloadsUpdated?: number;

    // For generic notifications
    notificationType?: 'success' | 'error' | 'info' | 'warning';

    // Cancellation flag
    cancelled?: boolean;
  };

  error?: string;
}

interface NotificationsContextType {
  notifications: UnifiedNotification[];
  addNotification: (notification: Omit<UnifiedNotification, 'id' | 'startedAt'>) => string;
  updateNotification: (id: string, updates: Partial<UnifiedNotification>) => void;
  removeNotification: (id: string) => void;
  clearCompletedNotifications: () => void;
}

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

// Helper function to check if notifications should auto-dismiss
const shouldAutoDismiss = (): boolean => {
  return !themeService.getPicsAlwaysVisible();
};

export const NotificationsProvider: React.FC<NotificationsProviderProps> = ({ children }) => {
  const [notifications, setNotifications] = useState<UnifiedNotification[]>([]);
  const signalR = useSignalR();

  // Helper function to remove notification with animation
  const removeNotificationAnimated = useCallback((id: string) => {
    // Dispatch event to trigger animation
    window.dispatchEvent(
      new CustomEvent('notification-removing', {
        detail: { notificationId: id }
      })
    );

    // Wait for animation to complete, then remove
    setTimeout(() => {
      setNotifications((prev) => prev.filter((n) => n.id !== id));
    }, NOTIFICATION_ANIMATION_DURATION_MS);
  }, []);

  // Helper function to schedule auto-dismiss for notifications
  const scheduleAutoDismiss = useCallback(
    (notificationId: string, delayMs: number = AUTO_DISMISS_DELAY_MS) => {
      if (shouldAutoDismiss()) {
        setTimeout(() => {
          removeNotificationAnimated(notificationId);
        }, delayMs);
      }
    },
    [removeNotificationAnimated]
  );

  const addNotification = useCallback(
    (notification: Omit<UnifiedNotification, 'id' | 'startedAt'>): string => {
      // For game_removal, use gameAppId in ID so SignalR handler can find it
      const id =
        notification.type === 'game_removal' && notification.details?.gameAppId
          ? `${notification.type}-${notification.details.gameAppId}`
          : `${notification.type}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

      const newNotification: UnifiedNotification = {
        ...notification,
        id,
        startedAt: new Date()
      };
      setNotifications((prev) => [...prev, newNotification]);

      // Auto-dismiss completed and failed notifications
      if (notification.status === 'completed' || notification.status === 'failed') {
        scheduleAutoDismiss(id);
      }

      return id;
    },
    [scheduleAutoDismiss]
  );

  const updateNotification = useCallback(
    (id: string, updates: Partial<UnifiedNotification>) => {
      setNotifications((prev) => prev.map((n) => (n.id === id ? { ...n, ...updates } : n)));

      // Auto-dismiss if updating to completed or failed status
      if (updates.status === 'completed' || updates.status === 'failed') {
        scheduleAutoDismiss(id);
      }
    },
    [scheduleAutoDismiss]
  );

  const removeNotification = useCallback((id: string) => {
    setNotifications((prev) => prev.filter((n) => n.id !== id));
  }, []);

  const clearCompletedNotifications = useCallback(() => {
    setNotifications((prev) => prev.filter((n) => n.status === 'running'));
  }, []);

  // SignalR Event Handlers - centralized notification management
  React.useEffect(() => {
    // Log Processing Progress
    const handleProcessingProgress = (payload: any) => {
      const currentProgress = payload.percentComplete || payload.progress || 0;
      const status = payload.status || 'processing';

      if (status === 'complete') {
        // Find existing notification and update it
        const existing = notifications.find(
          (n) => n.type === 'log_processing' && n.status === 'running'
        );
        if (existing) {
          updateNotification(existing.id, {
            status: 'completed',
            message: 'Processing Complete!',
            detailMessage: `Successfully processed ${payload.entriesProcessed?.toLocaleString() || 0} entries`,
            progress: 100
          });
        }
      } else {
        const message = `Processing: ${payload.mbProcessed?.toFixed(1) || 0} MB of ${payload.mbTotal?.toFixed(1) || 0} MB`;
        const detailMessage = `${payload.entriesProcessed?.toLocaleString() || 0} of ${payload.totalLines?.toLocaleString() || 0} entries`;

        const existing = notifications.find(
          (n) => n.type === 'log_processing' && n.status === 'running'
        );
        if (existing) {
          updateNotification(existing.id, {
            message,
            detailMessage,
            progress: Math.min(99.9, currentProgress),
            details: {
              ...existing.details,
              mbProcessed: payload.mbProcessed,
              mbTotal: payload.mbTotal,
              entriesProcessed: payload.entriesProcessed,
              totalLines: payload.totalLines
            }
          });
        } else {
          // Create new notification
          addNotification({
            type: 'log_processing',
            status: 'running',
            message,
            detailMessage,
            progress: Math.min(99.9, currentProgress),
            details: {
              mbProcessed: payload.mbProcessed,
              mbTotal: payload.mbTotal,
              entriesProcessed: payload.entriesProcessed,
              totalLines: payload.totalLines
            }
          });
        }
      }
    };

    const handleBulkProcessingComplete = (result: any) => {
      const existing = notifications.find((n) => n.type === 'log_processing');
      if (existing) {
        updateNotification(existing.id, {
          status: 'completed',
          message: 'Processing Complete!',
          detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString() || 0} entries from ${result.linesProcessed?.toLocaleString() || 0} lines in ${result.elapsed?.toFixed(1) || 0} minutes.`,
          progress: 100
        });
      }
    };

    // Service Log Removal
    const handleLogRemovalProgress = (payload: any) => {
      const notificationId = `service_removal-${payload.service}`;

      if (payload.status === 'starting' || payload.status === 'removing') {
        // Create message that shows removal count during processing
        const linesRemoved = payload.linesRemoved || 0;
        const message =
          linesRemoved > 0
            ? `Removing ${payload.service} entries (${linesRemoved.toLocaleString()} removed)...`
            : payload.message || `Removing ${payload.service} entries...`;

        const existing = notifications.find(
          (n) => n.id === notificationId && n.status === 'running'
        );
        if (existing) {
          updateNotification(notificationId, {
            message,
            progress: payload.percentComplete || 0,
            details: {
              ...existing.details,
              service: payload.service,
              linesProcessed: payload.linesProcessed || 0,
              linesRemoved: payload.linesRemoved || 0
            }
          });
        } else {
          // Create notification with fixed ID based on service, removing any old completed/failed ones first
          setNotifications((prev) => {
            const filtered = prev.filter((n) => n.id !== notificationId);
            return [
              ...filtered,
              {
                id: notificationId,
                type: 'service_removal',
                status: 'running',
                message,
                progress: payload.percentComplete || 0,
                startedAt: new Date(),
                details: {
                  service: payload.service,
                  linesProcessed: payload.linesProcessed || 0,
                  linesRemoved: payload.linesRemoved || 0
                }
              }
            ];
          });
        }
      }
    };

    const handleLogRemovalComplete = (payload: any) => {
      const notificationId = `service_removal-${payload.service}`;

      if (payload.success) {
        updateNotification(notificationId, {
          status: 'completed',
          message:
            payload.message ||
            `Successfully removed ${payload.service} log entries (${payload.linesProcessed || 0} lines processed)`,
          progress: 100
        });
      } else {
        updateNotification(notificationId, {
          status: 'failed',
          error: payload.message || 'Removal failed'
        });
      }
    };

    // Game Removal Complete
    const handleGameRemovalComplete = (payload: any) => {
      console.log('[NotificationsContext] GameRemovalComplete received:', payload);
      const notificationId = `game_removal-${payload.gameAppId}`;
      console.log('[NotificationsContext] Looking for notification ID:', notificationId);

      const existing = notifications.find((n) => n.id === notificationId);
      if (!existing) return;

      if (payload.success) {
        updateNotification(notificationId, {
          status: 'completed',
          details: {
            ...existing.details,
            filesDeleted: payload.filesDeleted,
            bytesFreed: payload.bytesFreed,
            logEntriesRemoved: payload.logEntriesRemoved
          }
        });
      } else {
        updateNotification(notificationId, {
          status: 'failed',
          error: payload.message || 'Removal failed'
        });
      }
    };

    // Database Reset
    const handleDatabaseResetProgress = (payload: any) => {
      const notificationId = 'database-reset';

      if (payload.status === 'complete') {
        updateNotification(notificationId, {
          status: 'completed',
          message: payload.message || 'Database reset completed',
          progress: 100
        });
      } else if (payload.status === 'error') {
        updateNotification(notificationId, {
          status: 'failed',
          error: payload.message
        });
      } else {
        const existing = notifications.find(
          (n) => n.id === notificationId && n.status === 'running'
        );
        if (existing) {
          updateNotification(notificationId, {
            message: payload.message || 'Resetting database...',
            progress: payload.percentComplete || 0
          });
        } else {
          // Create notification with fixed ID, removing any old completed/failed ones first
          setNotifications((prev) => {
            const filtered = prev.filter((n) => n.id !== notificationId);
            return [
              ...filtered,
              {
                id: notificationId,
                type: 'database_reset',
                status: 'running',
                message: payload.message || 'Resetting database...',
                progress: payload.percentComplete || 0,
                startedAt: new Date()
              }
            ];
          });
        }
      }
    };

    // Cache Clear Progress
    const handleCacheClearProgress = (payload: any) => {
      const notificationId = 'cache_clearing';

      const existing = notifications.find((n) => n.id === notificationId && n.status === 'running');
      if (existing) {
        updateNotification(notificationId, {
          message: payload.statusMessage || 'Clearing cache...',
          progress: payload.percentComplete || 0,
          details: {
            ...existing.details,
            filesDeleted: payload.filesDeleted || 0,
            directoriesProcessed: payload.directoriesProcessed || 0,
            bytesDeleted: payload.bytesDeleted || 0
          }
        });
      } else {
        // Create notification with fixed ID, removing any old completed/failed ones first
        setNotifications((prev) => {
          const filtered = prev.filter((n) => n.id !== notificationId);
          return [
            ...filtered,
            {
              id: notificationId,
              type: 'cache_clearing',
              status: 'running',
              message: payload.statusMessage || 'Clearing cache...',
              progress: payload.percentComplete || 0,
              startedAt: new Date(),
              details: {
                filesDeleted: payload.filesDeleted || 0,
                directoriesProcessed: payload.directoriesProcessed || 0,
                bytesDeleted: payload.bytesDeleted || 0
              }
            }
          ];
        });
      }
    };

    // Cache Clear Complete
    const handleCacheClearComplete = (payload: any) => {
      console.log('[NotificationsContext] CacheClearComplete received:', payload);
      const notificationId = 'cache_clearing';

      const existing = notifications.find((n) => n.id === notificationId);
      if (!existing) return;

      if (payload.success) {
        updateNotification(notificationId, {
          status: 'completed',
          message: payload.message || 'Cache cleared successfully',
          details: {
            ...existing.details,
            filesDeleted: payload.filesDeleted,
            directoriesProcessed: payload.directoriesProcessed
          }
        });
      } else {
        updateNotification(notificationId, {
          status: 'failed',
          error: payload.error || payload.message || 'Cache clear failed'
        });
      }
    };

    // Depot Mapping Started
    const handleDepotMappingStarted = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingStarted received:', payload);
      const notificationId = 'depot_mapping';

      // Remove any existing depot mapping notifications (including completed/failed ones) and add new one
      setNotifications((prev) => {
        const filtered = prev.filter((n) => n.id !== notificationId);
        return [
          ...filtered,
          {
            id: notificationId,
            type: 'depot_mapping',
            status: 'running',
            message: payload.message || 'Starting depot mapping scan...',
            startedAt: new Date(),
            progress: 0,
            details: {
              isLoggedOn: payload.isLoggedOn
            }
          }
        ];
      });
    };

    // Depot Mapping Progress
    const handleDepotMappingProgress = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingProgress received:', payload);
      const notificationId = 'depot_mapping';

      const existing = notifications.find((n) => n.id === notificationId);
      if (!existing) return;

      updateNotification(notificationId, {
        progress: payload.percentComplete || 0,
        message: payload.message || payload.status || 'Processing depot mappings...',
        details: {
          ...existing.details,
          isLoggedOn:
            payload.isLoggedOn !== undefined ? payload.isLoggedOn : existing.details?.isLoggedOn
        },
        detailMessage: (() => {
          // PICS scan progress (processedBatches exists)
          if (payload.processedBatches !== undefined && payload.totalBatches !== undefined) {
            return `${payload.processedBatches.toLocaleString()} / ${payload.totalBatches.toLocaleString()} batches${
              payload.depotMappingsFound !== undefined
                ? ` • ${payload.depotMappingsFound.toLocaleString()} mappings found`
                : ''
            }`;
          }
          // Download mapping progress (processedMappings exists)
          if (payload.processedMappings !== undefined && payload.totalMappings !== undefined) {
            return `${payload.processedMappings.toLocaleString()} / ${payload.totalMappings.toLocaleString()} downloads${
              payload.mappingsApplied !== undefined
                ? ` • ${payload.mappingsApplied.toLocaleString()} mappings applied`
                : ''
            }`;
          }
          return undefined;
        })()
      });
    };

    // Depot Mapping Complete (added as new handler)
    const handleDepotMappingComplete = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingComplete received:', payload);
      const notificationId = 'depot_mapping';

      // Handle cancellation
      if (payload.cancelled) {
        const existing = notifications.find((n) => n.id === notificationId);
        if (existing) {
          updateNotification(notificationId, {
            status: 'completed',
            message: 'Depot mapping scan cancelled',
            progress: 100,
            details: {
              ...existing.details,
              cancelled: true
            }
          });
          // Use shorter delay for cancelled operations
          scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
        }
        return;
      }

      if (payload.success) {
        // Check if this was an incremental scan - if so, animate to 100% first
        const isIncremental = payload.scanMode === 'incremental';

        if (isIncremental) {
          // Animate progress to 100%
          const animationDuration = INCREMENTAL_SCAN_ANIMATION_DURATION_MS;
          const steps = INCREMENTAL_SCAN_ANIMATION_STEPS;
          const interval = animationDuration / steps;

          setNotifications((prev) => {
            const notification = prev.find((n) => n.id === notificationId);
            if (!notification) return prev;

            const startProgress = notification.progress || 0;
            const progressIncrement = (100 - startProgress) / steps;
            let currentStep = 0;

            const animationInterval = setInterval(() => {
              currentStep++;
              const newProgress = Math.min(100, startProgress + progressIncrement * currentStep);

              setNotifications((prevNotes) =>
                prevNotes.map((n) =>
                  n.id === notificationId
                    ? {
                        ...n,
                        progress: newProgress,
                        message:
                          newProgress >= 100
                            ? payload.message || 'Depot mapping completed successfully'
                            : n.message
                      }
                    : n
                )
              );

              if (currentStep >= steps) {
                clearInterval(animationInterval);

                // After animation, show completion status
                setTimeout(() => {
                  const existing = notifications.find((n) => n.id === notificationId);
                  if (existing) {
                    updateNotification(notificationId, {
                      status: 'completed',
                      message: payload.message || 'Depot mapping completed successfully',
                      details: {
                        ...existing.details,
                        totalMappings: payload.totalMappings,
                        downloadsUpdated: payload.downloadsUpdated
                      }
                    });
                  }
                }, NOTIFICATION_ANIMATION_DURATION_MS);
              }
            }, interval);

            return prev;
          });
        } else {
          // Full scan - show completion immediately
          const existing = notifications.find((n) => n.id === notificationId);
          if (existing) {
            updateNotification(notificationId, {
              status: 'completed',
              message: payload.message || `Depot mapping completed successfully`,
              details: {
                ...existing.details,
                totalMappings: payload.totalMappings,
                downloadsUpdated: payload.downloadsUpdated
              }
            });
          }
        }
      } else {
        updateNotification(notificationId, {
          status: 'failed',
          error: payload.error || payload.message || 'Depot mapping failed'
        });
      }
    };

    // Subscribe to events
    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('BulkProcessingComplete', handleBulkProcessingComplete);
    signalR.on('LogRemovalProgress', handleLogRemovalProgress);
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('GameRemovalComplete', handleGameRemovalComplete);
    signalR.on('CacheClearProgress', handleCacheClearProgress);
    signalR.on('CacheClearComplete', handleCacheClearComplete);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);

    // Cleanup
    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('BulkProcessingComplete', handleBulkProcessingComplete);
      signalR.off('LogRemovalProgress', handleLogRemovalProgress);
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('GameRemovalComplete', handleGameRemovalComplete);
      signalR.off('CacheClearProgress', handleCacheClearProgress);
      signalR.off('CacheClearComplete', handleCacheClearComplete);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotMappingComplete', handleDepotMappingComplete);
    };
  }, [
    signalR,
    notifications,
    updateNotification,
    addNotification,
    scheduleAutoDismiss,
    removeNotificationAnimated
  ]);

  // Listen for changes to the "Always Visible" setting
  React.useEffect(() => {
    const handlePicsVisibilityChange = () => {
      // When setting is disabled (auto-dismiss is now enabled), start timers for existing completed/failed notifications
      notifications.forEach((notification) => {
        if (notification.status === 'completed' || notification.status === 'failed') {
          // Use shorter delay for cancelled depot mapping operations
          const timeout =
            notification.type === 'depot_mapping' && notification.details?.cancelled
              ? CANCELLED_NOTIFICATION_DELAY_MS
              : AUTO_DISMISS_DELAY_MS;

          scheduleAutoDismiss(notification.id, timeout);
        }
      });
    };

    window.addEventListener('picsvisibilitychange', handlePicsVisibilityChange);
    return () => window.removeEventListener('picsvisibilitychange', handlePicsVisibilityChange);
  }, [notifications, scheduleAutoDismiss]);

  // Universal Recovery: Check all backend operations on mount
  React.useEffect(() => {
    const recoverAllOperations = async () => {
      try {
        // Run all recovery checks in parallel
        await Promise.allSettled([
          recoverLogProcessing(),
          recoverServiceRemoval(),
          recoverDepotMapping(),
          recoverCacheClearing(),
          recoverDatabaseReset(),
          recoverGameDetection()
        ]);
      } catch (err) {
        console.error('[NotificationsContext] Failed to recover operations:', err);
      }
    };

    const recoverLogProcessing = async () => {
      try {
        const response = await fetch('/api/management/processing-status');
        if (response.ok) {
          const data = await response.json();

          if (data.isProcessing) {
            const notificationId = 'log_processing';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              const message = `Processing: ${data.mbProcessed?.toFixed(1) || 0} MB of ${data.mbTotal?.toFixed(1) || 0} MB`;
              const detailMessage = `${data.entriesProcessed?.toLocaleString() || 0} of ${data.totalLines?.toLocaleString() || 0} entries`;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'log_processing' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message,
                  detailMessage,
                  progress: Math.min(99.9, data.percentComplete || 0),
                  startedAt: new Date(),
                  details: {
                    mbProcessed: data.mbProcessed,
                    mbTotal: data.mbTotal,
                    entriesProcessed: data.entriesProcessed,
                    totalLines: data.totalLines
                  }
                }
              ];
            });

            console.log('[NotificationsContext] Recovered log processing notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    const recoverServiceRemoval = async () => {
      try {
        const response = await fetch('/api/management/logs/remove-status');
        if (response.ok) {
          const data = await response.json();

          if (data.isProcessing && data.service) {
            const notificationId = 'service_removal';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'service_removal' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message: `Removing ${data.service} entries from logs`,
                  progress: data.percentComplete || 0,
                  startedAt: new Date(),
                  details: {
                    service: data.service,
                    linesProcessed: data.linesProcessed,
                    linesRemoved: data.linesRemoved
                  }
                }
              ];
            });

            console.log('[NotificationsContext] Recovered service removal notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    const recoverDepotMapping = async () => {
      try {
        const response = await fetch('/api/gameinfo/steamkit/progress');
        if (response.ok) {
          const data = await response.json();

          if (data.isRunning) {
            const notificationId = 'depot_mapping';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'depot_mapping' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message: data.status || 'Processing depot mappings...',
                  startedAt: new Date(),
                  progress: data.progressPercent || 0,
                  details: {
                    isLoggedOn: data.isLoggedOn
                  },
                  detailMessage: (() => {
                    if (data.processedBatches !== undefined && data.totalBatches !== undefined) {
                      return `${data.processedBatches.toLocaleString()} / ${data.totalBatches.toLocaleString()} batches${
                        data.depotMappingsFound !== undefined
                          ? ` • ${data.depotMappingsFound.toLocaleString()} mappings found`
                          : ''
                      }`;
                    }
                    return undefined;
                  })()
                }
              ];
            });

            console.log('[NotificationsContext] Recovered depot mapping notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    const recoverCacheClearing = async () => {
      try {
        const response = await fetch('/api/management/cache/active-operations');
        if (response.ok) {
          const data = await response.json();

          // Check if there are any active operations
          if (data.hasActive && data.operations && data.operations.length > 0) {
            // Get the first running operation
            const activeOp = data.operations[0];
            const notificationId = 'cache_clearing';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'cache_clearing' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message: activeOp.statusMessage || 'Clearing cache...',
                  progress: activeOp.percentComplete || 0,
                  startedAt: new Date(),
                  details: {
                    filesDeleted: activeOp.filesDeleted || 0,
                    directoriesProcessed: activeOp.directoriesProcessed || 0,
                    bytesDeleted: activeOp.bytesDeleted || 0
                  }
                }
              ];
            });

            console.log('[NotificationsContext] Recovered cache clearing notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    const recoverDatabaseReset = async () => {
      try {
        const response = await fetch('/api/management/database/reset-status');
        if (response.ok) {
          const data = await response.json();

          if (data.isProcessing) {
            const notificationId = 'database-reset';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'database_reset' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message: data.message || 'Resetting database...',
                  progress: data.percentComplete || 0,
                  startedAt: new Date()
                }
              ];
            });

            console.log('[NotificationsContext] Recovered database reset notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    const recoverGameDetection = async () => {
      try {
        const response = await fetch('/api/management/cache/detect-games-active');
        if (response.ok) {
          const data = await response.json();

          if (data.hasActiveOperation && data.operation) {
            const op = data.operation;
            const notificationId = 'game_detection';

            setNotifications((prev) => {
              const existing = prev.find((n) => n.id === notificationId);
              if (existing) return prev;

              return [
                ...prev,
                {
                  id: notificationId,
                  type: 'generic' as NotificationType,
                  status: 'running' as NotificationStatus,
                  message: op.statusMessage || 'Detecting games in cache...',
                  progress: op.percentComplete || 0,
                  startedAt: new Date(),
                  details: {
                    notificationType: 'info' as const
                  }
                }
              ];
            });

            console.log('[NotificationsContext] Recovered game detection notification');
          }
        }
      } catch (err) {
        // Silently fail - operation not running
      }
    };

    recoverAllOperations();
  }, []); // Run once on mount

  const value = {
    notifications,
    addNotification,
    updateNotification,
    removeNotification,
    clearCompletedNotifications
  };

  return <NotificationsContext.Provider value={value}>{children}</NotificationsContext.Provider>;
};
