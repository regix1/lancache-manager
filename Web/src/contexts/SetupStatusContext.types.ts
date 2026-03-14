import { createContext } from 'react';

interface SetupStatus {
  isCompleted: boolean;
  hasProcessedLogs: boolean;
  isSetupCompleted: boolean;
}

interface SetupStatusContextType {
  setupStatus: SetupStatus | null;
  isLoading: boolean;
  refreshSetupStatus: () => Promise<void>;
  markSetupCompleted: () => void;
}

export const SetupStatusContext = createContext<SetupStatusContextType | undefined>(undefined);
