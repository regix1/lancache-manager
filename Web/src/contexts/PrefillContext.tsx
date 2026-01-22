import React, {
  createContext,
  useContext,
  useState,
  useCallback,
  useRef,
  type ReactNode
} from 'react';
import {
  createLogEntry,
  type LogEntry,
  type LogEntryType
} from '@components/features/prefill/ActivityLog';

const STORAGE_KEY = 'prefill_activity_log';
const BACKGROUND_COMPLETION_KEY = 'prefill_background_completion';
const DISMISSED_COMPLETION_KEY = 'prefill_dismissed_completion_at';
const MAX_LOG_ENTRIES = 500; // Limit stored entries to prevent storage bloat

export interface BackgroundCompletion {
  completedAt: string;
  message: string;
  duration?: number; // Duration in seconds if available
}

interface PrefillContextType {
  logEntries: LogEntry[];
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  clearLogs: () => void;
  // Session state that persists across tab switches
  sessionId: string | null;
  setSessionId: (id: string | null) => void;
  // Background completion notification
  backgroundCompletion: BackgroundCompletion | null;
  setBackgroundCompletion: (completion: BackgroundCompletion | null) => void;
  clearBackgroundCompletion: () => void;
  // Track dismissed completion to prevent re-showing
  isCompletionDismissed: (completedAt: string) => boolean;
  // Clear all prefill-related storage (for session end/cleanup)
  clearAllPrefillStorage: () => void;
}

const PrefillContext = createContext<PrefillContextType | undefined>(undefined);

export const usePrefillContext = () => {
  const context = useContext(PrefillContext);
  if (!context) {
    throw new Error('usePrefillContext must be used within PrefillProvider');
  }
  return context;
};

interface PrefillProviderProps {
  children: ReactNode;
}

// Helper to restore logs from sessionStorage
const restoreLogsFromStorage = (): LogEntry[] => {
  try {
    const saved = sessionStorage.getItem(STORAGE_KEY);
    if (saved) {
      const parsed = JSON.parse(saved);
      // Convert timestamp strings back to Date objects
      return parsed.map((entry: LogEntry & { timestamp: string }) => ({
        ...entry,
        timestamp: new Date(entry.timestamp)
      }));
    }
  } catch (error) {
    console.error('[PrefillContext] Failed to restore logs:', error);
  }
  return [];
};

// Helper to save logs to sessionStorage
const saveLogsToStorage = (entries: LogEntry[]) => {
  try {
    // Keep only the most recent entries to prevent storage bloat
    const entriesToSave = entries.slice(-MAX_LOG_ENTRIES);
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(entriesToSave));
  } catch (error) {
    console.error('[PrefillContext] Failed to save logs:', error);
  }
};

// Helper to restore background completion from sessionStorage
const restoreBackgroundCompletion = (): BackgroundCompletion | null => {
  try {
    const saved = sessionStorage.getItem(BACKGROUND_COMPLETION_KEY);
    if (saved) {
      return JSON.parse(saved);
    }
  } catch (error) {
    console.error('[PrefillContext] Failed to restore background completion:', error);
  }
  return null;
};

export const PrefillProvider: React.FC<PrefillProviderProps> = ({ children }) => {
  const [logEntries, setLogEntries] = useState<LogEntry[]>(() => restoreLogsFromStorage());
  const [sessionId, setSessionIdState] = useState<string | null>(() => {
    try {
      return sessionStorage.getItem('prefill_session_id');
    } catch {
      return null;
    }
  });
  const [backgroundCompletion, setBackgroundCompletionState] =
    useState<BackgroundCompletion | null>(() => restoreBackgroundCompletion());

  // Use ref to track if we need to persist (prevents excessive writes)
  const pendingSaveRef = useRef<NodeJS.Timeout | null>(null);

  const persistLogs = useCallback((entries: LogEntry[]) => {
    // Debounce saves to prevent excessive writes
    if (pendingSaveRef.current) {
      clearTimeout(pendingSaveRef.current);
    }
    pendingSaveRef.current = setTimeout(() => {
      saveLogsToStorage(entries);
      pendingSaveRef.current = null;
    }, 100);
  }, []);

  const addLog = useCallback(
    (type: LogEntryType, message: string, details?: string) => {
      setLogEntries((prev) => {
        const newEntries = [...prev, createLogEntry(type, message, details)];
        persistLogs(newEntries);
        return newEntries;
      });
    },
    [persistLogs]
  );

  const clearLogs = useCallback(() => {
    setLogEntries([]);
    try {
      sessionStorage.removeItem(STORAGE_KEY);
    } catch {
      // Ignore errors
    }
  }, []);

  const setSessionId = useCallback((id: string | null) => {
    setSessionIdState(id);
    try {
      if (id) {
        sessionStorage.setItem('prefill_session_id', id);
      } else {
        sessionStorage.removeItem('prefill_session_id');
      }
    } catch {
      // Ignore errors
    }
  }, []);

  const setBackgroundCompletion = useCallback((completion: BackgroundCompletion | null) => {
    setBackgroundCompletionState(completion);
    try {
      if (completion) {
        sessionStorage.setItem(BACKGROUND_COMPLETION_KEY, JSON.stringify(completion));
      } else {
        sessionStorage.removeItem(BACKGROUND_COMPLETION_KEY);
      }
    } catch {
      // Ignore errors
    }
  }, []);

  const clearBackgroundCompletion = useCallback(() => {
    // Record the completedAt timestamp before clearing so we don't re-show it
    const current = backgroundCompletion;
    if (current?.completedAt) {
      try {
        sessionStorage.setItem(DISMISSED_COMPLETION_KEY, current.completedAt);
      } catch {
        // Ignore errors
      }
    }
    setBackgroundCompletionState(null);
    try {
      sessionStorage.removeItem(BACKGROUND_COMPLETION_KEY);
    } catch {
      // Ignore errors
    }
  }, [backgroundCompletion]);

  // Check if a completion with a specific timestamp has been dismissed
  // Uses a 60-second window to account for client/server timestamp differences
  const isCompletionDismissed = useCallback((completedAt: string): boolean => {
    try {
      const dismissedAt = sessionStorage.getItem(DISMISSED_COMPLETION_KEY);
      if (!dismissedAt) return false;

      // Parse both timestamps and compare within a 60-second window
      const dismissedTime = new Date(dismissedAt).getTime();
      const completedTime = new Date(completedAt).getTime();

      // If timestamps are within 60 seconds of each other, consider it dismissed
      // This handles client/server timestamp differences
      return Math.abs(dismissedTime - completedTime) < 60000;
    } catch {
      return false;
    }
  }, []);

  // Clear all prefill-related storage (for session end/cleanup)
  const clearAllPrefillStorage = useCallback(() => {
    try {
      // Clear all prefill-related sessionStorage keys
      sessionStorage.removeItem(STORAGE_KEY); // prefill_activity_log
      sessionStorage.removeItem(BACKGROUND_COMPLETION_KEY); // prefill_background_completion
      sessionStorage.removeItem(DISMISSED_COMPLETION_KEY); // prefill_dismissed_completion_at
      sessionStorage.removeItem('prefill_session_id');
      sessionStorage.removeItem('prefill_in_progress');

      // Reset local state
      setLogEntries([]);
      setSessionIdState(null);
      setBackgroundCompletionState(null);

      console.log('[PrefillContext] Cleared all prefill storage');
    } catch (error) {
      console.error('[PrefillContext] Failed to clear prefill storage:', error);
    }
  }, []);

  const value = {
    logEntries,
    addLog,
    clearLogs,
    sessionId,
    setSessionId,
    backgroundCompletion,
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage
  };

  return <PrefillContext.Provider value={value}>{children}</PrefillContext.Provider>;
};
