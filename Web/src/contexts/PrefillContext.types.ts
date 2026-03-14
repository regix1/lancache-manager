import { createContext } from 'react';
import type { LogEntry, LogEntryType } from '@components/features/prefill/ActivityLog.utils';
import type { BackgroundCompletion } from '@components/features/prefill/hooks/prefillTypes';

interface PrefillContextType {
  logEntries: LogEntry[];
  addLog: (type: LogEntryType, message: string, details?: string) => void;
  clearLogs: () => void;
  // Background completion notification
  backgroundCompletion: BackgroundCompletion | null;
  setBackgroundCompletion: (completion: BackgroundCompletion | null) => void;
  clearBackgroundCompletion: () => void;
  // Track dismissed completion to prevent re-showing
  isCompletionDismissed: (completedAt: string) => boolean;
  // Clear all prefill-related storage (for session end/cleanup)
  clearAllPrefillStorage: () => void;
}

export const PrefillContext = createContext<PrefillContextType | undefined>(undefined);
