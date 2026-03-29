import { createContext } from 'react';

interface WizardStateUpdate {
  currentSetupStep?: string | null;
  dataSourceChoice?: string | null;
  completedPlatforms?: string | null;
}

export interface SetupStatus {
  isCompleted: boolean;
  hasProcessedLogs: boolean;
  needsPostgresCredentials: boolean;
  currentSetupStep: string | null;
  dataSourceChoice: string | null;
  completedPlatforms: string | null;
}

interface SetupStatusContextType {
  setupStatus: SetupStatus | null;
  isLoading: boolean;
  syncError: string | null;
  refreshSetupStatus: () => Promise<void>;
  markSetupCompleted: () => void;
  updateWizardState: (updates: WizardStateUpdate) => Promise<boolean>;
}

export const SetupStatusContext = createContext<SetupStatusContextType | undefined>(undefined);
