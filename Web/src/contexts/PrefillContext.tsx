import React, { useState, useCallback, useEffect, useRef, type ReactNode } from 'react';
import {
  createLogEntry,
  type LogEntry,
  type LogEntryType
} from '@components/features/prefill/ActivityLog.utils';
import {
  isPrefillRunActive,
  mergePrefillRuns,
  prefillRunKey,
  retainPrefillCompletions,
  type PrefillCompletion,
  type PrefillRun
} from '@components/features/prefill/hooks/prefillTypes';
import { PrefillContext } from './PrefillContext.types';
import { STORAGE_KEYS } from '@utils/constants';
import { sessionStore } from '@utils/storage';

const STORAGE_KEY = 'prefill_activity_log';
const RUN_COMPLETIONS_KEY = 'prefill_run_completions';
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

export const PrefillProvider: React.FC<PrefillProviderProps> = ({ children }) => {
  const [runCompletions, setRunCompletions] = useState<PrefillRun[]>([]);
  const completionsRef = useRef<PrefillCompletion[]>(
    retainPrefillCompletions(sessionStore.getJSON<PrefillCompletion[]>(RUN_COMPLETIONS_KEY) ?? [])
  );
  const recordRunCompletion = useCallback((run: PrefillRun) => {
    if (isPrefillRunActive(run)) return;
    const key = prefillRunKey(run);
    const entries = retainPrefillCompletions(completionsRef.current);
    if (!entries.some((entry) => entry.key === key)) {
      entries.push({ key, completedAt: Date.now(), dismissed: false });
    }
    completionsRef.current = retainPrefillCompletions(entries);
    sessionStore.setJSON(RUN_COMPLETIONS_KEY, completionsRef.current);
    setRunCompletions((current) => {
      const previous = current.find((entry) => prefillRunKey(entry) === key);
      if (previous && previous.snapshot.sequence >= run.snapshot.sequence) return current;
      return mergePrefillRuns(current, [run]).filter((entry) =>
        completionsRef.current.some(
          (completion) => completion.key === prefillRunKey(entry) && !completion.dismissed
        )
      );
    });
  }, []);
  const dismissRunCompletion = useCallback((run: PrefillRun) => {
    const key = prefillRunKey(run);
    completionsRef.current = retainPrefillCompletions([
      ...completionsRef.current.filter((entry) => entry.key !== key),
      { key, completedAt: Date.now(), dismissed: true }
    ]);
    sessionStore.setJSON(RUN_COMPLETIONS_KEY, completionsRef.current);
    setRunCompletions((current) => current.filter((entry) => prefillRunKey(entry) !== key));
  }, []);
  const isRunCompletionDismissed = useCallback(
    (run: PrefillRun) =>
      retainPrefillCompletions(completionsRef.current).some(
        (entry) => entry.key === prefillRunKey(run) && entry.dismissed
      ),
    []
  );
  const [logEntries, setLogEntries] = useState<LogEntry[]>(() => restoreLogsFromStorage());

  // Track recent log messages for deduplication (prevents duplicate SignalR events from showing multiple times)
  const recentLogsRef = useRef<Map<string, number>>(new Map());

  // Saves the committed log, debounced to prevent excessive writes. A cleared log commits an empty list,
  // which cancels the pending save, so the old log is never written back after a session end or a clear.
  useEffect(() => {
    if (logEntries.length === 0) return;
    const pendingSave = setTimeout(() => saveLogsToStorage(logEntries), 100);
    return () => clearTimeout(pendingSave);
  }, [logEntries]);

  const addLog = useCallback((type: LogEntryType, message: string, details?: string) => {
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
      return [...prev, createLogEntry(type, message, details)].slice(-MAX_LOG_ENTRIES);
    });
  }, []);

  const clearLogs = useCallback(() => {
    setLogEntries([]);
    sessionStore.removeItem(STORAGE_KEY);
  }, []);

  // Clear all prefill-related storage (for session end/cleanup)
  const clearAllPrefillStorage = useCallback(() => {
    // Clear all prefill-related sessionStorage keys
    sessionStore.removeItem(STORAGE_KEY); // prefill_activity_log
    sessionStore.removeItem(STORAGE_KEYS.PREFILL_SESSION_ID);

    // Reset local state
    setLogEntries([]);
  }, []);

  const value = {
    runCompletions,
    recordRunCompletion,
    dismissRunCompletion,
    isRunCompletionDismissed,
    logEntries,
    addLog,
    clearLogs,
    clearAllPrefillStorage
  };

  return <PrefillContext.Provider value={value}>{children}</PrefillContext.Provider>;
};
