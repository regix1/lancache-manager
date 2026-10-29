import { createContext } from 'react';
import type { LogEntry, LogEntryType } from '@components/features/prefill/ActivityLog.utils';
import type { PrefillRun } from '@components/features/prefill/hooks/prefillTypes';

interface PrefillContextType {
  runCompletions: PrefillRun[];
  recordRunCompletion: (run: PrefillRun) => void;
  dismissRunCompletion: (run: PrefillRun) => void;
  isRunCompletionDismissed: (run: PrefillRun) => boolean;
  logEntries: LogEntry[];
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  clearLogs: () => void;
  // Clear all prefill-related storage (for session end/cleanup)
  clearAllPrefillStorage: () => void;
}

export const PrefillContext = createContext<PrefillContextType | undefined>(undefined);
