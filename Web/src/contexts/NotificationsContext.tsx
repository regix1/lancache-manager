import React, {
  createContext,
  useContext,
  useState,
  useCallback,
  type ReactNode
} from 'react';
import { useSignalR } from './SignalRContext';
import themeService from '@services/theme.service';

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
    window.dispatchEvent(new CustomEvent('notification-removing', {
      detail: { notificationId: id }
    }));

    // Wait for animation to complete, then remove
    setTimeout(() => {
      setNotifications(prev => prev.filter(n => n.id !== id));
    }, 300); // Match animation duration
  }, []);

  const addNotification = useCallback((notification: Omit<UnifiedNotification, 'id' | 'startedAt'>): string => {
    // For game_removal, use gameAppId in ID so SignalR handler can find it
    const id = notification.type === 'game_removal' && notification.details?.gameAppId
      ? `${notification.type}-${notification.details.gameAppId}`
      : `${notification.type}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

    const newNotification: UnifiedNotification = {
      ...notification,
      id,
      startedAt: new Date()
    };
    setNotifications(prev => [...prev, newNotification]);

    // Auto-dismiss completed and failed notifications (unless always visible is enabled)
    if ((notification.status === 'completed' || notification.status === 'failed') && shouldAutoDismiss()) {
      setTimeout(() => {
        removeNotificationAnimated(id);
      }, 5000); // Remove after 5 seconds
    }

    return id;
  }, [removeNotificationAnimated]);

  const updateNotification = useCallback((id: string, updates: Partial<UnifiedNotification>) => {
    setNotifications(prev =>
      prev.map(n => (n.id === id ? { ...n, ...updates } : n))
    );

    // Auto-dismiss if updating to completed or failed status (unless always visible is enabled)
    if ((updates.status === 'completed' || updates.status === 'failed') && shouldAutoDismiss()) {
      setTimeout(() => {
        removeNotificationAnimated(id);
      }, 5000); // Remove after 5 seconds
    }
  }, [removeNotificationAnimated]);

  const removeNotification = useCallback((id: string) => {
    setNotifications(prev => prev.filter(n => n.id !== id));
  }, []);

  const clearCompletedNotifications = useCallback(() => {
    setNotifications(prev => prev.filter(n => n.status === 'running'));
  }, []);

  // SignalR Event Handlers - centralized notification management
  React.useEffect(() => {
    // Log Processing Progress
    const handleProcessingProgress = (payload: any) => {
      const currentProgress = payload.percentComplete || payload.progress || 0;
      const status = payload.status || 'processing';

      if (status === 'complete') {
        // Find existing notification and update it
        setNotifications(prev => {
          const existing = prev.find(n => n.type === 'log_processing' && n.status === 'running');
          if (existing) {
            return prev.map(n =>
              n.id === existing.id
                ? {
                    ...n,
                    status: 'completed' as NotificationStatus,
                    message: 'Processing Complete!',
                    detailMessage: `Successfully processed ${payload.entriesProcessed?.toLocaleString() || 0} entries`,
                    progress: 100
                  }
                : n
            );
          }
          return prev;
        });

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            // Get current notifications at time of timeout
            setNotifications(prev => {
              const completed = prev.find(n => n.type === 'log_processing' && n.status === 'completed');
              if (completed) {
                removeNotificationAnimated(completed.id);
              }
              return prev;
            });
          }, 5000);
        }
      } else {
        const message = `Processing: ${payload.mbProcessed?.toFixed(1) || 0} MB of ${payload.mbTotal?.toFixed(1) || 0} MB`;
        const detailMessage = `${payload.entriesProcessed?.toLocaleString() || 0} of ${payload.totalLines?.toLocaleString() || 0} entries`;

        setNotifications(prev => {
          const existing = prev.find(n => n.type === 'log_processing' && n.status === 'running');
          if (existing) {
            return prev.map(n =>
              n.id === existing.id
                ? {
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
                  }
                : n
            );
          } else {
            // Create new notification
            const id = `log_processing-${Date.now()}`;
            return [...prev, {
              id,
              type: 'log_processing' as NotificationType,
              status: 'running' as NotificationStatus,
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
            }];
          }
        });
      }
    };

    const handleBulkProcessingComplete = (result: any) => {
      setNotifications(prev => {
        const existing = prev.find(n => n.type === 'log_processing');
        if (existing) {
          return prev.map(n =>
            n.id === existing.id
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  message: 'Processing Complete!',
                  detailMessage: `Successfully processed ${result.entriesProcessed?.toLocaleString() || 0} entries from ${result.linesProcessed?.toLocaleString() || 0} lines in ${result.elapsed?.toFixed(1) || 0} minutes.`,
                  progress: 100
                }
              : n
          );
        }
        return prev;
      });

      // Auto-remove after 5 seconds (unless always visible is enabled)
      if (shouldAutoDismiss()) {
        setTimeout(() => {
          setNotifications(prev => {
            const completed = prev.find(n => n.type === 'log_processing' && n.status === 'completed');
            if (completed) {
              removeNotificationAnimated(completed.id);
            }
            return prev;
          });
        }, 5000);
      }
    };

    // Service Log Removal
    const handleLogRemovalProgress = (payload: any) => {
      const notificationId = `service_removal-${payload.service}`;

      if (payload.status === 'starting' || payload.status === 'removing') {
        setNotifications(prev => {
          const existing = prev.find(n => n.id === notificationId);
          if (existing) {
            return prev.map(n =>
              n.id === notificationId
                ? {
                    ...n,
                    message: payload.message || `Removing ${payload.service} entries...`,
                    progress: payload.percentComplete || 0,
                    details: {
                      ...n.details,
                      service: payload.service,
                      linesProcessed: payload.linesProcessed || 0,
                      linesRemoved: payload.linesRemoved || 0
                    }
                  }
                : n
            );
          } else {
            return [...prev, {
              id: notificationId,
              type: 'service_removal' as NotificationType,
              status: 'running' as NotificationStatus,
              message: payload.message || `Removing ${payload.service} entries...`,
              progress: payload.percentComplete || 0,
              startedAt: new Date(),
              details: {
                service: payload.service,
                linesProcessed: payload.linesProcessed || 0,
                linesRemoved: payload.linesRemoved || 0
              }
            }];
          }
        });
      }
    };

    const handleLogRemovalComplete = (payload: any) => {
      const notificationId = `service_removal-${payload.service}`;

      if (payload.success) {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  message: payload.message || `Removed ${payload.linesRemoved || 0} ${payload.service} entries`,
                  progress: 100
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      } else {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'failed' as NotificationStatus,
                  error: payload.message || 'Removal failed'
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      }
    };

    // Game Removal Complete
    const handleGameRemovalComplete = (payload: any) => {
      console.log('[NotificationsContext] GameRemovalComplete received:', payload);
      const notificationId = `game_removal-${payload.gameAppId}`;
      console.log('[NotificationsContext] Looking for notification ID:', notificationId);

      if (payload.success) {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  details: {
                    ...n.details,
                    filesDeleted: payload.filesDeleted,
                    bytesFreed: payload.bytesFreed,
                    logEntriesRemoved: payload.logEntriesRemoved
                  }
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      } else {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'failed' as NotificationStatus,
                  error: payload.message || 'Removal failed'
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      }
    };

    // Database Reset
    const handleDatabaseResetProgress = (payload: any) => {
      const notificationId = 'database-reset';

      if (payload.status === 'complete') {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  message: 'Database reset completed',
                  progress: 100
                }
              : n
          )
        );
      } else if (payload.status === 'error') {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'failed' as NotificationStatus,
                  error: payload.message
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      } else {
        setNotifications(prev => {
          const existing = prev.find(n => n.id === notificationId);
          if (existing) {
            return prev.map(n =>
              n.id === notificationId
                ? {
                    ...n,
                    message: payload.message || 'Resetting database...',
                    progress: payload.percentComplete || 0
                  }
                : n
            );
          } else {
            return [...prev, {
              id: notificationId,
              type: 'database_reset' as NotificationType,
              status: 'running' as NotificationStatus,
              message: payload.message || 'Resetting database...',
              progress: payload.percentComplete || 0,
              startedAt: new Date()
            }];
          }
        });
      }
    };

    // Cache Clear Progress
    const handleCacheClearProgress = (payload: any) => {
      const notificationId = 'cache_clearing';

      setNotifications(prev => {
        const existing = prev.find(n => n.id === notificationId);
        if (existing) {
          return prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  message: payload.statusMessage || 'Clearing cache...',
                  progress: payload.percentComplete || 0,
                  details: {
                    ...n.details,
                    filesDeleted: payload.filesDeleted || 0,
                    directoriesProcessed: payload.directoriesProcessed || 0,
                    bytesDeleted: payload.bytesDeleted || 0
                  }
                }
              : n
          );
        } else {
          return [...prev, {
            id: notificationId,
            type: 'cache_clearing' as NotificationType,
            status: 'running' as NotificationStatus,
            message: payload.statusMessage || 'Clearing cache...',
            progress: payload.percentComplete || 0,
            startedAt: new Date(),
            details: {
              filesDeleted: payload.filesDeleted || 0,
              directoriesProcessed: payload.directoriesProcessed || 0,
              bytesDeleted: payload.bytesDeleted || 0
            }
          }];
        }
      });
    };

    // Cache Clear Complete
    const handleCacheClearComplete = (payload: any) => {
      console.log('[NotificationsContext] CacheClearComplete received:', payload);
      const notificationId = 'cache_clearing';

      if (payload.success) {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  message: payload.message || 'Cache cleared successfully',
                  details: {
                    ...n.details,
                    filesDeleted: payload.filesDeleted,
                    directoriesProcessed: payload.directoriesProcessed
                  }
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      } else {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'failed' as NotificationStatus,
                  error: payload.error || payload.message || 'Cache clear failed'
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      }
    };

    // Depot Mapping Started
    const handleDepotMappingStarted = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingStarted received:', payload);
      const notificationId = 'depot_mapping';

      // Remove any existing depot mapping notifications and add new one with fixed ID
      setNotifications(prev => {
        const filtered = prev.filter(n => n.id !== notificationId);
        return [...filtered, {
          id: notificationId,
          type: 'depot_mapping' as NotificationType,
          status: 'running' as NotificationStatus,
          message: payload.message || 'Starting depot mapping scan...',
          startedAt: new Date(),
          progress: 0,
          details: {
            isLoggedOn: payload.isLoggedOn
          }
        }];
      });
    };

    // Depot Mapping Progress
    const handleDepotMappingProgress = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingProgress received:', payload);
      const notificationId = 'depot_mapping';
      setNotifications(prev =>
        prev.map(n =>
          n.id === notificationId
            ? {
                ...n,
                progress: payload.percentComplete || 0,
                message: payload.message || payload.status || 'Processing depot mappings...',
                details: {
                  ...n.details,
                  isLoggedOn: payload.isLoggedOn !== undefined ? payload.isLoggedOn : n.details?.isLoggedOn
                },
                detailMessage: (() => {
                  // PICS scan progress (processedBatches exists)
                  if (payload.processedBatches !== undefined && payload.totalBatches !== undefined) {
                    return `${payload.processedBatches.toLocaleString()} / ${payload.totalBatches.toLocaleString()} batches${
                      payload.depotMappingsFound !== undefined ? ` • ${payload.depotMappingsFound.toLocaleString()} mappings found` : ''
                    }`;
                  }
                  // Download mapping progress (processedMappings exists)
                  if (payload.processedMappings !== undefined && payload.totalMappings !== undefined) {
                    return `${payload.processedMappings.toLocaleString()} / ${payload.totalMappings.toLocaleString()} downloads${
                      payload.mappingsApplied !== undefined ? ` • ${payload.mappingsApplied.toLocaleString()} mappings applied` : ''
                    }`;
                  }
                  return undefined;
                })()
              }
            : n
        )
      );
    };

    // Depot Mapping Complete (added as new handler)
    const handleDepotMappingComplete = (payload: any) => {
      console.log('[NotificationsContext] DepotMappingComplete received:', payload);
      const notificationId = 'depot_mapping';

      // Handle cancellation
      if (payload.cancelled) {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'completed' as NotificationStatus,
                  message: 'Depot mapping scan cancelled',
                  progress: 100,
                  details: {
                    ...n.details,
                    cancelled: true
                  }
                }
              : n
          )
        );

        // Auto-remove after 3 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 3000);
        }
        return;
      }

      if (payload.success) {
        // Check if this was an incremental scan - if so, animate to 100% first
        const isIncremental = payload.scanMode === 'incremental';

        if (isIncremental) {
          // Animate progress to 100% over 1.5 seconds
          const animationDuration = 1500; // ms
          const steps = 30;
          const interval = animationDuration / steps;

          setNotifications(prev => {
            const notification = prev.find(n => n.id === notificationId);
            if (!notification) return prev;

            const startProgress = notification.progress || 0;
            const progressIncrement = (100 - startProgress) / steps;
            let currentStep = 0;

            const animationInterval = setInterval(() => {
              currentStep++;
              const newProgress = Math.min(100, startProgress + (progressIncrement * currentStep));

              setNotifications(prevNotes =>
                prevNotes.map(n =>
                  n.id === notificationId
                    ? {
                        ...n,
                        progress: newProgress,
                        message: newProgress >= 100
                          ? (payload.message || 'Depot mapping completed successfully')
                          : n.message
                      }
                    : n
                )
              );

              if (currentStep >= steps) {
                clearInterval(animationInterval);

                // After animation, show completion status
                setTimeout(() => {
                  setNotifications(prevNotes =>
                    prevNotes.map(n =>
                      n.id === notificationId
                        ? {
                            ...n,
                            status: 'completed' as NotificationStatus,
                            message: payload.message || 'Depot mapping completed successfully',
                            details: {
                              ...n.details,
                              totalMappings: payload.totalMappings,
                              downloadsUpdated: payload.downloadsUpdated
                            }
                          }
                        : n
                    )
                  );

                  // Auto-remove after 5 seconds (unless always visible is enabled)
                  if (shouldAutoDismiss()) {
                    setTimeout(() => {
                      setNotifications(prevNotes => prevNotes.filter(n => n.id !== notificationId));
                    }, 5000);
                  }
                }, 300); // Small delay after reaching 100%
              }
            }, interval);

            return prev;
          });
        } else {
          // Full scan - show completion immediately
          setNotifications(prev =>
            prev.map(n =>
              n.id === notificationId
                ? {
                    ...n,
                    status: 'completed' as NotificationStatus,
                    message: payload.message || `Depot mapping completed successfully`,
                    details: {
                      ...n.details,
                      totalMappings: payload.totalMappings,
                      downloadsUpdated: payload.downloadsUpdated
                    }
                  }
                : n
            )
          );

          // Auto-remove after 5 seconds (unless always visible is enabled)
          if (shouldAutoDismiss()) {
            setTimeout(() => {
              removeNotificationAnimated(notificationId);
            }, 5000);
          }
        }
      } else {
        setNotifications(prev =>
          prev.map(n =>
            n.id === notificationId
              ? {
                  ...n,
                  status: 'failed' as NotificationStatus,
                  error: payload.error || payload.message || 'Depot mapping failed'
                }
              : n
          )
        );

        // Auto-remove after 5 seconds (unless always visible is enabled)
        if (shouldAutoDismiss()) {
          setTimeout(() => {
            removeNotificationAnimated(notificationId);
          }, 5000);
        }
      }
    };

    // Depot Post Processing Failed (placeholder for compatibility)
    const handleDepotPostProcessingFailed = (payload: any) => {
      console.log('[NotificationsContext] DepotPostProcessingFailed received:', payload);
      addNotification({
        type: 'depot_mapping',
        status: 'failed',
        message: payload.message || 'Depot post-processing failed',
        error: payload.error,
        details: {
          notificationType: 'error'
        }
      });
    };

    // Pics Progress - DISABLED, now using DepotMappingProgress events instead
    // This old handler was creating duplicate notifications and interfering with the new depot mapping notification
    const handlePicsProgress = () => {
      // No-op: Depot mapping progress is now handled by DepotMappingStarted/Progress/Complete events
      // This prevents duplicate notifications and UI conflicts
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
    signalR.on('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
    signalR.on('PicsProgress', handlePicsProgress);

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
      signalR.off('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
      signalR.off('PicsProgress', handlePicsProgress);
    };
  }, [signalR, removeNotificationAnimated]);

  // Listen for changes to the "Always Visible" setting
  React.useEffect(() => {
    const handlePicsVisibilityChange = () => {
      // When setting is disabled (auto-dismiss is now enabled), start timers for existing completed/failed notifications
      if (shouldAutoDismiss()) {
        notifications.forEach(notification => {
          if (notification.status === 'completed' || notification.status === 'failed') {
            // Determine timeout based on notification type
            const timeout = notification.type === 'depot_mapping' && notification.details?.cancelled ? 3000 : 5000;

            setTimeout(() => {
              removeNotificationAnimated(notification.id);
            }, timeout);
          }
        });
      }
    };

    window.addEventListener('picsvisibilitychange', handlePicsVisibilityChange);
    return () => window.removeEventListener('picsvisibilitychange', handlePicsVisibilityChange);
  }, [notifications, removeNotificationAnimated]);

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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              const message = `Processing: ${data.mbProcessed?.toFixed(1) || 0} MB of ${data.mbTotal?.toFixed(1) || 0} MB`;
              const detailMessage = `${data.entriesProcessed?.toLocaleString() || 0} of ${data.totalLines?.toLocaleString() || 0} entries`;

              return [...prev, {
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
              }];
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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              return [...prev, {
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
              }];
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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              return [...prev, {
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
                      data.depotMappingsFound !== undefined ? ` • ${data.depotMappingsFound.toLocaleString()} mappings found` : ''
                    }`;
                  }
                  return undefined;
                })()
              }];
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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              return [...prev, {
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
              }];
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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              return [...prev, {
                id: notificationId,
                type: 'database_reset' as NotificationType,
                status: 'running' as NotificationStatus,
                message: data.message || 'Resetting database...',
                progress: data.percentComplete || 0,
                startedAt: new Date()
              }];
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

            setNotifications(prev => {
              const existing = prev.find(n => n.id === notificationId);
              if (existing) return prev;

              return [...prev, {
                id: notificationId,
                type: 'generic' as NotificationType,
                status: 'running' as NotificationStatus,
                message: op.statusMessage || 'Detecting games in cache...',
                progress: op.percentComplete || 0,
                startedAt: new Date(),
                details: {
                  notificationType: 'info' as const
                }
              }];
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

  return (
    <NotificationsContext.Provider value={value}>
      {children}
    </NotificationsContext.Provider>
  );
};
