import React, {
  createContext,
  useContext,
  useState,
  useCallback,
  type ReactNode
} from 'react';
import { useSignalR } from './SignalRContext';

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

    // For generic notifications
    notificationType?: 'success' | 'error' | 'info' | 'warning';
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

export const NotificationsProvider: React.FC<NotificationsProviderProps> = ({ children }) => {
  const [notifications, setNotifications] = useState<UnifiedNotification[]>([]);
  const signalR = useSignalR();

  const addNotification = useCallback((notification: Omit<UnifiedNotification, 'id' | 'startedAt'>): string => {
    const id = `${notification.type}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
    const newNotification: UnifiedNotification = {
      ...notification,
      id,
      startedAt: new Date()
    };
    setNotifications(prev => [...prev, newNotification]);

    // Auto-dismiss completed and failed notifications
    if (notification.status === 'completed' || notification.status === 'failed') {
      setTimeout(() => {
        setNotifications(prev => prev.filter(n => n.id !== id));
      }, 5000); // Remove after 5 seconds
    }

    return id;
  }, []);

  const updateNotification = useCallback((id: string, updates: Partial<UnifiedNotification>) => {
    setNotifications(prev =>
      prev.map(n => (n.id === id ? { ...n, ...updates } : n))
    );

    // Auto-dismiss if updating to completed or failed status
    if (updates.status === 'completed' || updates.status === 'failed') {
      setTimeout(() => {
        setNotifications(prev => prev.filter(n => n.id !== id));
      }, 5000); // Remove after 5 seconds
    }
  }, []);

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

        // Auto-remove after 5 seconds
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.type !== 'log_processing' || n.status !== 'completed'));
        }, 5000);
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

      // Auto-remove after 5 seconds
      setTimeout(() => {
        setNotifications(prev => prev.filter(n => n.type !== 'log_processing' || n.status !== 'completed'));
      }, 5000);
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

        // Auto-remove after 5 seconds
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
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

        // Auto-remove after 5 seconds
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
      }
    };

    // Game Removal Complete
    const handleGameRemovalComplete = (payload: any) => {
      const notificationId = `game_removal-${payload.gameAppId}`;

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

        // Auto-remove after 5 seconds
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
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

        // Auto-remove after 5 seconds
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
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
                  message: 'Database reset completed - redirecting to home...',
                  progress: 100
                }
              : n
          )
        );

        // Wait for database to fully reset before redirect
        setTimeout(() => {
          window.location.href = '/';
        }, 2500);
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

        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
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

    // Depot Mapping
    const handleDepotMappingStarted = (payload: any) => {
      const notificationId = 'depot-mapping';
      setNotifications(prev => {
        const existing = prev.find(n => n.id === notificationId);
        if (!existing) {
          return [...prev, {
            id: notificationId,
            type: 'depot_mapping' as NotificationType,
            status: 'running' as NotificationStatus,
            message: payload.message || 'Starting depot mapping post-processing...',
            startedAt: new Date(),
            details: {
              isProcessing: true,
              totalMappings: 0,
              processedMappings: 0,
              percentComplete: 0
            }
          }];
        }
        return prev;
      });
    };

    const handleDepotMappingProgress = (payload: any) => {
      const notificationId = 'depot-mapping';
      setNotifications(prev =>
        prev.map(n =>
          n.id === notificationId
            ? {
                ...n,
                message: payload.message,
                details: {
                  isProcessing: payload.isProcessing,
                  totalMappings: payload.totalMappings,
                  processedMappings: payload.processedMappings,
                  mappingsApplied: payload.mappingsApplied,
                  percentComplete: payload.percentComplete
                }
              }
            : n
        )
      );

      // Clear when complete
      if (!payload.isProcessing || payload.status === 'complete') {
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
      }
    };

    const handleDepotPostProcessingFailed = (payload: any) => {
      setNotifications(prev => prev.filter(n => n.type !== 'depot_mapping'));
      // Add error notification
      const errorId = `generic-error-${Date.now()}`;
      setNotifications(prev => [...prev, {
        id: errorId,
        type: 'generic' as NotificationType,
        status: 'failed' as NotificationStatus,
        message: payload?.error
          ? `Depot mapping post-processing failed: ${payload.error}`
          : 'Depot mapping post-processing failed.',
        startedAt: new Date(),
        details: {
          notificationType: 'error'
        }
      }]);

      setTimeout(() => {
        setNotifications(prev => prev.filter(n => n.id !== errorId));
      }, 5000);
    };

    // PICS Scan Progress
    const handlePicsProgress = (payload: any) => {
      const notificationId = 'pics-scan';

      if (payload.status === 'complete' || !payload.isRunning) {
        // Remove the notification when complete
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notificationId));
        }, 5000);
      } else if (payload.isRunning) {
        setNotifications(prev => {
          const existing = prev.find(n => n.id === notificationId);
          const message = `Scanning Steam: ${payload.processedApps || 0}/${payload.totalApps || 0} apps`;
          const progress = payload.progressPercent || 0;

          if (existing) {
            return prev.map(n =>
              n.id === notificationId
                ? {
                    ...n,
                    message,
                    progress,
                    details: {
                      ...n.details,
                      totalApps: payload.totalApps,
                      processedApps: payload.processedApps,
                      depotMappingsFound: payload.depotMappingsFound
                    }
                  }
                : n
            );
          } else {
            return [...prev, {
              id: notificationId,
              type: 'depot_mapping' as NotificationType,
              status: 'running' as NotificationStatus,
              message,
              progress,
              startedAt: new Date(),
              details: {
                totalApps: payload.totalApps,
                processedApps: payload.processedApps,
                depotMappingsFound: payload.depotMappingsFound
              }
            }];
          }
        });
      }
    };

    // Subscribe to events
    signalR.on('ProcessingProgress', handleProcessingProgress);
    signalR.on('BulkProcessingComplete', handleBulkProcessingComplete);
    signalR.on('LogRemovalProgress', handleLogRemovalProgress);
    signalR.on('LogRemovalComplete', handleLogRemovalComplete);
    signalR.on('GameRemovalComplete', handleGameRemovalComplete);
    signalR.on('DatabaseResetProgress', handleDatabaseResetProgress);
    signalR.on('DepotMappingStarted', handleDepotMappingStarted);
    signalR.on('DepotMappingProgress', handleDepotMappingProgress);
    signalR.on('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
    signalR.on('PicsProgress', handlePicsProgress);

    // Cleanup
    return () => {
      signalR.off('ProcessingProgress', handleProcessingProgress);
      signalR.off('BulkProcessingComplete', handleBulkProcessingComplete);
      signalR.off('LogRemovalProgress', handleLogRemovalProgress);
      signalR.off('LogRemovalComplete', handleLogRemovalComplete);
      signalR.off('GameRemovalComplete', handleGameRemovalComplete);
      signalR.off('DatabaseResetProgress', handleDatabaseResetProgress);
      signalR.off('DepotMappingStarted', handleDepotMappingStarted);
      signalR.off('DepotMappingProgress', handleDepotMappingProgress);
      signalR.off('DepotPostProcessingFailed', handleDepotPostProcessingFailed);
      signalR.off('PicsProgress', handlePicsProgress);
    };
  }, [signalR]);

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
