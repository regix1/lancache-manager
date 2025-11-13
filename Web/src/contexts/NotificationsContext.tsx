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
  | 'corruption_removal'
  | 'database_reset'
  | 'depot_mapping'
  | 'game_detection'
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

    // For game_detection
    operationId?: string;
    scanType?: 'full' | 'incremental';
    totalGamesDetected?: number;
    totalServicesDetected?: number;

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
      // For service_removal, use service name in ID so SignalR handler can find it
      let id: string;
      if (notification.type === 'game_removal' && notification.details?.gameAppId) {
        id = `${notification.type}-${notification.details.gameAppId}`;
      } else if (notification.type === 'service_removal' && notification.details?.service) {
        id = `${notification.type}-${notification.details.service}`;
      } else {
        id = `${notification.type}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
      }

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
        // Find existing notification and update it, or create one if it doesn't exist (handles race condition)
        let notificationId: string | null = null;
        setNotifications((prev) => {
          const existing = prev.find((n) => n.type === 'log_processing' && n.status === 'running');

          if (!existing) {
            // Processing completed so fast that the starting event's state update hasn't committed yet
            // Create a notification and immediately mark it as complete
            const id = `log_processing-${Date.now()}`;
            notificationId = id;
            return [
              ...prev,
              {
                id,
                type: 'log_processing',
                status: 'completed' as const,
                message: 'Processing Complete!',
                detailMessage: `Successfully processed ${payload.entriesProcessed?.toLocaleString() || 0} entries`,
                progress: 100,
                startedAt: new Date()
              }
            ];
          }

          notificationId = existing.id; // Capture ID for auto-dismiss

          const updated = prev.map((n) => {
            if (n.id === existing.id) {
              return {
                ...n,
                status: 'completed' as const,
                message: 'Processing Complete!',
                detailMessage: `Successfully processed ${payload.entriesProcessed?.toLocaleString() || 0} entries`,
                progress: 100
              };
            }
            return n;
          });

          return updated;
        });

        // Schedule auto-dismiss using captured ID
        if (notificationId) {
          scheduleAutoDismiss(notificationId);
        }
      } else {
        const message = `Processing: ${payload.mbProcessed?.toFixed(1) || 0} MB of ${payload.mbTotal?.toFixed(1) || 0} MB`;
        const detailMessage = `${payload.entriesProcessed?.toLocaleString() || 0} of ${payload.totalLines?.toLocaleString() || 0} entries`;

        // Use setNotifications to get current state (avoid stale closure)
        setNotifications((prev) => {
          const existing = prev.find((n) => n.type === 'log_processing' && n.status === 'running');
          if (existing) {
            return prev.map((n) => {
              if (n.id === existing.id) {
                return {
                  ...n,
                  message,
                  detailMessage,
                  progress: Math.min(99.9, currentProgress),
                  details: {
                    ...n.details,
                    mbProcessed: payload.mbProcessed,
                    mbTotal: payload.mbTotal,
                    entriesProcessed: payload.entriesProcessed,
                    totalLines: payload.totalLines
                  }
                };
              }
              return n;
            });
          } else {
            // Create new notification
            const id = `log_processing-${Date.now()}`;
            return [
              ...prev,
              {
                id,
                type: 'log_processing',
                status: 'running',
                message,
                detailMessage,
                progress: Math.min(99.9, currentProgress),
                startedAt: new Date(),
                details: {
                  mbProcessed: payload.mbProcessed,
                  mbTotal: payload.mbTotal,
                  entriesProcessed: payload.entriesProcessed,
                  totalLines: payload.totalLines
                }
              }
            ];
          }
        });
      }
    };

    const handleFastProcessingComplete = (result: any) => {
      let notificationId: string | null = null;
      setNotifications((prev) => {
        const existing = prev.find((n) => n.type === 'log_processing');

        if (!existing) {
          // Processing completed so fast that no notification was created yet (race condition)
          // Create a notification and immediately mark it as complete
          const id = `log_processing-${Date.now()}`;
          notificationId = id;
          return [
            ...prev,
            {
              id,
              type: 'log_processing',
              status: 'completed' as const,
              message: 'Processing Complete!',
              detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString() || 0} entries from ${result.linesProcessed?.toLocaleString() || 0} lines in ${result.elapsed?.toFixed(1) || 0} minutes.`,
              progress: 100,
              startedAt: new Date()
            }
          ];
        }

        notificationId = existing.id; // Capture ID for auto-dismiss

        const updated = prev.map((n) => {
          if (n.id === existing.id) {
            return {
              ...n,
              status: 'completed' as const,
              message: 'Processing Complete!',
              detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString() || 0} entries from ${result.linesProcessed?.toLocaleString() || 0} lines in ${result.elapsed?.toFixed(1) || 0} minutes.`,
              progress: 100
            };
          }
          return n;
        });

        return updated;
      });

      // Schedule auto-dismiss using captured ID
      if (notificationId) {
        scheduleAutoDismiss(notificationId);
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

        // Use setNotifications to get current state (avoid stale closure)
        setNotifications((prev) => {
          const existing = prev.find((n) => n.id === notificationId && n.status === 'running');
          if (existing) {
            return prev.map((n) => {
              if (n.id === notificationId) {
                return {
                  ...n,
                  message,
                  progress: payload.percentComplete || 0,
                  details: {
                    ...n.details,
                    service: payload.service,
                    linesProcessed: payload.linesProcessed || 0,
                    linesRemoved: payload.linesRemoved || 0
                  }
                };
              }
              return n;
            });
          } else {
            // Create notification with fixed ID based on service, removing any old completed/failed ones first
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
          }
        });
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

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) {
          console.warn('[NotificationsContext] No existing notification found for:', notificationId);
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (payload.success) {
              return {
                ...n,
                status: 'completed' as const,
                details: {
                  ...n.details,
                  filesDeleted: payload.filesDeleted,
                  bytesFreed: payload.bytesFreed,
                  logEntriesRemoved: payload.logEntriesRemoved
                }
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: payload.message || 'Removal failed'
              };
            }
          }
          return n;
        });
      });

      // Schedule auto-dismiss
      scheduleAutoDismiss(notificationId);
    };

    // Service Removal Complete
    const handleServiceRemovalComplete = (payload: any) => {
      console.log('[NotificationsContext] ServiceRemovalComplete received:', payload);
      const notificationId = `service_removal-${payload.serviceName}`;
      console.log('[NotificationsContext] Looking for notification ID:', notificationId);

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) {
          console.warn('[NotificationsContext] No existing notification found for:', notificationId);
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (payload.success) {
              return {
                ...n,
                status: 'completed' as const,
                details: {
                  ...n.details,
                  filesDeleted: payload.filesDeleted,
                  bytesFreed: payload.bytesFreed,
                  logEntriesRemoved: payload.logEntriesRemoved
                }
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: payload.message || 'Removal failed'
              };
            }
          }
          return n;
        });
      });

      // Schedule auto-dismiss
      scheduleAutoDismiss(notificationId);
    };

    // Corruption Removal Started
    const handleCorruptionRemovalStarted = (payload: any) => {
      console.log('[NotificationsContext] CorruptionRemovalStarted received:', payload);
      const notificationId = `corruption_removal-${payload.service}`;

      // Remove any existing corruption removal notifications for this service and add new one
      setNotifications((prev) => {
        const filtered = prev.filter((n) => n.id !== notificationId);
        return [
          ...filtered,
          {
            id: notificationId,
            type: 'corruption_removal',
            status: 'running',
            message: payload.message || `Removing corrupted chunks for ${payload.service}...`,
            startedAt: new Date(),
            progress: 0
          }
        ];
      });
    };

    // Corruption Removal Complete
    const handleCorruptionRemovalComplete = (payload: any) => {
      console.log('[NotificationsContext] CorruptionRemovalComplete received:', payload);
      const notificationId = `corruption_removal-${payload.service}`;

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) {
          console.warn('[NotificationsContext] No existing notification found for:', notificationId);
          return prev;
        }

        // Update the notification
        const updated = prev.map((n) => {
          if (n.id === notificationId) {
            if (payload.success) {
              return {
                ...n,
                status: 'completed' as const,
                message: payload.message || `Successfully removed corrupted chunks for ${payload.service}`,
                progress: 100
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: payload.error || payload.message || 'Corruption removal failed'
              };
            }
          }
          return n;
        });

        return updated;
      });

      // Schedule auto-dismiss
      if (payload.success || !payload.success) {
        scheduleAutoDismiss(notificationId);
      }
    };

    // Game Detection Started
    const handleGameDetectionStarted = (payload: any) => {
      console.log('[NotificationsContext] GameDetectionStarted received:', payload);
      const notificationId = `game_detection-${payload.operationId}`;

      // Remove any existing game detection notifications and add new one
      setNotifications((prev) => {
        const filtered = prev.filter((n) => n.type !== 'game_detection');
        return [
          ...filtered,
          {
            id: notificationId,
            type: 'game_detection',
            status: 'running',
            message: payload.message || 'Running game cache detection...',
            startedAt: new Date(),
            progress: 0,
            details: {
              operationId: payload.operationId,
              scanType: payload.scanType
            }
          }
        ];
      });
    };

    // Game Detection Complete
    const handleGameDetectionComplete = (payload: any) => {
      console.log('[NotificationsContext] GameDetectionComplete received:', payload);
      const notificationId = `game_detection-${payload.operationId}`;

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);

        if (!existing) {
          // Detection completed so fast that no notification was created yet (race condition)
          // Create a notification and immediately mark it as complete
          const id = `game_detection-${Date.now()}`;
          scheduleAutoDismiss(id);

          if (payload.success) {
            return [
              ...prev,
              {
                id,
                type: 'game_detection' as const,
                status: 'completed' as const,
                message: payload.message || 'Game detection complete',
                progress: 100,
                startedAt: new Date(),
                details: {
                  totalGamesDetected: payload.totalGamesDetected || 0,
                  totalServicesDetected: payload.totalServicesDetected || 0
                }
              }
            ];
          } else {
            return [
              ...prev,
              {
                id,
                type: 'game_detection' as const,
                status: 'failed' as const,
                message: payload.message || 'Game detection failed',
                startedAt: new Date(),
                error: payload.error || 'Detection failed'
              }
            ];
          }
        }

        // Update the notification
        const updated = prev.map((n) => {
          if (n.id === notificationId) {
            if (payload.success) {
              return {
                ...n,
                status: 'completed' as const,
                message: payload.message || 'Game detection complete',
                progress: 100,
                details: {
                  ...n.details,
                  totalGamesDetected: payload.totalGamesDetected || 0,
                  totalServicesDetected: payload.totalServicesDetected || 0
                }
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: payload.error || payload.message || 'Game detection failed'
              };
            }
          }
          return n;
        });

        return updated;
      });

      // Schedule auto-dismiss
      scheduleAutoDismiss(notificationId);
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
        // Use setNotifications to get current state (avoid stale closure)
        setNotifications((prev) => {
          const existing = prev.find((n) => n.id === notificationId && n.status === 'running');
          if (existing) {
            return prev.map((n) => {
              if (n.id === notificationId) {
                return {
                  ...n,
                  message: payload.message || 'Resetting database...',
                  progress: payload.percentComplete || 0
                };
              }
              return n;
            });
          } else {
            // Create notification with fixed ID, removing any old completed/failed ones first
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
          }
        });
      }
    };

    // Cache Clear Progress
    const handleCacheClearProgress = (payload: any) => {
      const notificationId = 'cache_clearing';

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId && n.status === 'running');
        if (existing) {
          return prev.map((n) => {
            if (n.id === notificationId) {
              return {
                ...n,
                message: payload.statusMessage || 'Clearing cache...',
                progress: payload.percentComplete || 0,
                details: {
                  ...n.details,
                  filesDeleted: payload.filesDeleted || 0,
                  directoriesProcessed: payload.directoriesProcessed || 0,
                  bytesDeleted: payload.bytesDeleted || 0
                }
              };
            }
            return n;
          });
        } else {
          // Create notification with fixed ID, removing any old completed/failed ones first
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
        }
      });
    };

    // Cache Clear Complete
    const handleCacheClearComplete = (payload: any) => {
      console.log('[NotificationsContext] CacheClearComplete received:', payload);
      const notificationId = 'cache_clearing';

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) {
          console.warn('[NotificationsContext] No existing notification found for:', notificationId);
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            if (payload.success) {
              return {
                ...n,
                status: 'completed' as const,
                message: payload.message || 'Cache cleared successfully',
                details: {
                  ...n.details,
                  filesDeleted: payload.filesDeleted,
                  directoriesProcessed: payload.directoriesProcessed
                }
              };
            } else {
              return {
                ...n,
                status: 'failed' as const,
                error: payload.error || payload.message || 'Cache clear failed'
              };
            }
          }
          return n;
        });
      });

      // Schedule auto-dismiss
      scheduleAutoDismiss(notificationId);
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

      // Use setNotifications to get current state (avoid stale closure)
      setNotifications((prev) => {
        const existing = prev.find((n) => n.id === notificationId);
        if (!existing) {
          console.warn('[NotificationsContext] No existing notification found for:', notificationId);
          return prev;
        }

        return prev.map((n) => {
          if (n.id === notificationId) {
            return {
              ...n,
              progress: payload.percentComplete || 0,
              message: payload.message || payload.status || 'Processing depot mappings...',
              details: {
                ...n.details,
                isLoggedOn:
                  payload.isLoggedOn !== undefined ? payload.isLoggedOn : n.details?.isLoggedOn
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
            };
          }
          return n;
        });
      });
    };

    // Depot Mapping Complete (added as new handler)
    const handleDepotMappingComplete = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingComplete received:', payload);
      const notificationId = 'depot_mapping';

      // Handle cancellation
      if (payload.cancelled) {
        setNotifications((prev) => {
          const existing = prev.find((n) => n.id === notificationId);
          if (!existing) {
            console.warn('[NotificationsContext] No existing notification found for:', notificationId);
            return prev;
          }

          return prev.map((n) => {
            if (n.id === notificationId) {
              return {
                ...n,
                status: 'completed' as const,
                message: 'Depot mapping scan cancelled',
                progress: 100,
                details: {
                  ...n.details,
                  cancelled: true
                }
              };
            }
            return n;
          });
        });
        // Use shorter delay for cancelled operations
        scheduleAutoDismiss(notificationId, CANCELLED_NOTIFICATION_DELAY_MS);
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
                  setNotifications((prevNotes) => {
                    const existing = prevNotes.find((n) => n.id === notificationId);
                    if (!existing) {
                      console.warn('[NotificationsContext] No existing notification found for:', notificationId);
                      return prevNotes;
                    }

                    return prevNotes.map((n) => {
                      if (n.id === notificationId) {
                        return {
                          ...n,
                          status: 'completed' as const,
                          message: payload.message || 'Depot mapping completed successfully',
                          details: {
                            ...n.details,
                            totalMappings: payload.totalMappings,
                            downloadsUpdated: payload.downloadsUpdated
                          }
                        };
                      }
                      return n;
                    });
                  });
                  scheduleAutoDismiss(notificationId);
                }, NOTIFICATION_ANIMATION_DURATION_MS);
              }
            }, interval);

            return prev;
          });
        } else {
          // Full scan - show completion immediately
          setNotifications((prev) => {
            const existing = prev.find((n) => n.id === notificationId);
            if (!existing) {
              console.warn('[NotificationsContext] No existing notification found for:', notificationId);
              return prev;
            }

            return prev.map((n) => {
              if (n.id === notificationId) {
                return {
                  ...n,
                  status: 'completed' as const,
                  message: payload.message || `Depot mapping completed successfully`,
                  details: {
                    ...n.details,
                    totalMappings: payload.totalMappings,
                    downloadsUpdated: payload.downloadsUpdated
                  }
                };
              }
              return n;
            });
          });
          scheduleAutoDismiss(notificationId);
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
    signalR.on('FastProcessingComplete', handleFastProcessingComplete);
    signalR.on('LogRemovalProgress', handleLogRemovalProgress);
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('GameRemovalComplete', handleGameRemovalComplete);
    signalR.on('ServiceRemovalComplete', handleServiceRemovalComplete);
    signalR.on('CorruptionRemovalStarted', handleCorruptionRemovalStarted);
    signalR.on('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
    signalR.on('GameDetectionStarted', handleGameDetectionStarted);
    signalR.on('GameDetectionComplete', handleGameDetectionComplete);
    signalR.on('CacheClearProgress', handleCacheClearProgress);
    signalR.on('CacheClearComplete', handleCacheClearComplete);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotMappingComplete', handleDepotMappingComplete);

    // Cleanup
    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('FastProcessingComplete', handleFastProcessingComplete);
      signalR.off('LogRemovalProgress', handleLogRemovalProgress);
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('GameRemovalComplete', handleGameRemovalComplete);
      signalR.off('ServiceRemovalComplete', handleServiceRemovalComplete);
      signalR.off('CorruptionRemovalStarted', handleCorruptionRemovalStarted);
      signalR.off('CorruptionRemovalComplete', handleCorruptionRemovalComplete);
      signalR.off('GameDetectionStarted', handleGameDetectionStarted);
      signalR.off('GameDetectionComplete', handleGameDetectionComplete);
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

  // Listen for custom toast notifications (e.g., preference changes)
  React.useEffect(() => {
    const handleShowToast = (event: any) => {
      const { type, message, duration } = event.detail;

      const notificationId = addNotification({
        type: 'generic',
        status: 'completed',
        message,
        details: {
          notificationType: type || 'info'
        }
      });

      // Auto-dismiss after specified duration or default
      if (shouldAutoDismiss()) {
        setTimeout(() => {
          removeNotificationAnimated(notificationId);
        }, duration || 4000);
      }
    };

    window.addEventListener('show-toast', handleShowToast);
    return () => window.removeEventListener('show-toast', handleShowToast);
  }, [addNotification, removeNotificationAnimated]);

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
                  message: op.statusMessage || 'Detecting games and services in cache...',
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
