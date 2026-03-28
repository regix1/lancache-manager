import { createContext } from 'react';

export interface WizardStateUpdate {
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
  refreshSetupStatus: () => Promise<void>;
  markSetupCompleted: () => void;
  updateWizardState: (updates: WizardStateUpdate) => Promise<void>;
}

export const SetupStatusContext = createContext<SetupStatusContextType | undefined>(undefined);
