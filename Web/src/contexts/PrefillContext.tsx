import React, { useState, useCallback, useRef, type ReactNode } from 'react';
import {
  createLogEntry,
  type LogEntry,
  type LogEntryType
} from '@components/features/prefill/ActivityLog.utils';
import type { BackgroundCompletion } from '@components/features/prefill/hooks/prefillTypes';
import { PrefillContext } from './PrefillContext.types';
import { STORAGE_KEYS } from '@utils/constants';
import { sessionStore } from '@utils/storage';

const STORAGE_KEY = 'prefill_activity_log';
const BACKGROUND_COMPLETION_KEY = 'prefill_background_completion';
const DISMISSED_COMPLETION_KEY = 'prefill_dismissed_completion_at';
const MAX_LOG_ENTRIES = 500; // Limit stored entries to prevent storage bloat
const LOG_DEDUPE_WINDOW_MS = 2000; // Deduplicate identical logs within 2 seconds

interface PrefillProviderProps {
  children: ReactNode;
}

// Helper to restore logs from sessionStorage. Called from a useState lazy initializer (render
// phase, before any provider - including NotificationsProvider - has mounted), so there is no
// notification channel reachable here even in principle. A corrupt/missing cache degrades
// gracefully to an empty log, which is harmless. Deliberately silent.
const restoreLogsFromStorage = (): LogEntry[] => {
  try {
    const saved = sessionStore.getItem(STORAGE_KEY);
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

// Helper to save logs to sessionStorage. Best-effort local persistence only (in-memory
// logEntries state still works this session even if the write fails); a full storage quota is
// the only realistic cause. Deliberately silent.
const saveLogsToStorage = (entries: LogEntry[]) => {
  // Keep only the most recent entries to prevent storage bloat
  sessionStore.setJSON(STORAGE_KEY, entries.slice(-MAX_LOG_ENTRIES));
};

// Helper to restore background completion from sessionStorage. Same render-phase constraint as
// restoreLogsFromStorage above - no notification channel is reachable here. Deliberately silent.
const restoreBackgroundCompletion = (): BackgroundCompletion | null =>
  sessionStore.getJSON<BackgroundCompletion>(BACKGROUND_COMPLETION_KEY);

export const PrefillProvider: React.FC<PrefillProviderProps> = ({ children }) => {
  const [logEntries, setLogEntries] = useState<LogEntry[]>(() => restoreLogsFromStorage());
  const [backgroundCompletion, setBackgroundCompletionState] =
    useState<BackgroundCompletion | null>(() => restoreBackgroundCompletion());

  // Use ref to track if we need to persist (prevents excessive writes)
  const pendingSaveRef = useRef<NodeJS.Timeout | null>(null);

  // Track recent log messages for deduplication (prevents duplicate SignalR events from showing multiple times)
  const recentLogsRef = useRef<Map<string, number>>(new Map());

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
      // Deduplicate: skip if the same type+message was added within the deduplication window
      // This prevents duplicate SignalR events from multiple connections showing multiple times
      const logKey = `${type}:${message}`;
      const now = Date.now();
      const lastSeen = recentLogsRef.current.get(logKey);

      if (lastSeen && now - lastSeen < LOG_DEDUPE_WINDOW_MS) {
        // Skip duplicate log entry
        return;
      }

      // Update the timestamp for this log key
      recentLogsRef.current.set(logKey, now);

      // Clean up old entries to prevent memory growth (keep map small)
      if (recentLogsRef.current.size > 50) {
        const cutoff = now - LOG_DEDUPE_WINDOW_MS;
        for (const [key, timestamp] of recentLogsRef.current) {
          if (timestamp < cutoff) {
            recentLogsRef.current.delete(key);
          }
        }
      }

      setLogEntries((prev) => {
        // Keep only the most recent entries so a long-running prefill doesn't grow state without
        // bound. Same cap the sessionStorage write applies.
        const newEntries = [...prev, createLogEntry(type, message, details)].slice(
          -MAX_LOG_ENTRIES
        );
        persistLogs(newEntries);
        return newEntries;
      });
    },
    [persistLogs]
  );

  const clearLogs = useCallback(() => {
    setLogEntries([]);
    sessionStore.removeItem(STORAGE_KEY);
  }, []);

  const setBackgroundCompletion = useCallback((completion: BackgroundCompletion | null) => {
    setBackgroundCompletionState(completion);
    if (completion) {
      sessionStore.setJSON(BACKGROUND_COMPLETION_KEY, completion);
    } else {
      sessionStore.removeItem(BACKGROUND_COMPLETION_KEY);
    }
  }, []);

  const clearBackgroundCompletion = useCallback(() => {
    // Record the completedAt timestamp before clearing so we don't re-show it
    const current = backgroundCompletion;
    if (current?.completedAt) {
      sessionStore.setItem(DISMISSED_COMPLETION_KEY, current.completedAt);
    }
    setBackgroundCompletionState(null);
    sessionStore.removeItem(BACKGROUND_COMPLETION_KEY);
  }, [backgroundCompletion]);

  // Check if a completion with a specific timestamp has been dismissed
  // Uses a 60-second window to account for client/server timestamp differences
  const isCompletionDismissed = useCallback((completedAt: string): boolean => {
    const dismissedAt = sessionStore.getItem(DISMISSED_COMPLETION_KEY);
    if (!dismissedAt) return false;

    // Parse both timestamps and compare within a 60-second window
    const dismissedTime = new Date(dismissedAt).getTime();
    const completedTime = new Date(completedAt).getTime();

    // If timestamps are within 60 seconds of each other, consider it dismissed
    // This handles client/server timestamp differences
    return Math.abs(dismissedTime - completedTime) < 60000;
  }, []);

  // Clear all prefill-related storage (for session end/cleanup)
  const clearAllPrefillStorage = useCallback(() => {
    // Clear all prefill-related sessionStorage keys
    sessionStore.removeItem(STORAGE_KEY); // prefill_activity_log
    sessionStore.removeItem(BACKGROUND_COMPLETION_KEY); // prefill_background_completion
    sessionStore.removeItem(DISMISSED_COMPLETION_KEY); // prefill_dismissed_completion_at
    sessionStore.removeItem(STORAGE_KEYS.PREFILL_SESSION_ID);
    sessionStore.removeItem(STORAGE_KEYS.PREFILL_IN_PROGRESS);

    // Reset local state
    setLogEntries([]);
    setBackgroundCompletionState(null);
  }, []);

  const value = {
    logEntries,
    addLog,
    clearLogs,
    backgroundCompletion,
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage
  };

  return <PrefillContext.Provider value={value}>{children}</PrefillContext.Provider>;
};
