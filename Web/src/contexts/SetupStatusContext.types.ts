import { createContext } from 'react';

interface WizardStateUpdate {
  currentSetupStep?: string | null;
  dataSourceChoice?: string | null;
  completedPlatforms?: string | null;
}

export type PostgresMode = 'embedded' | 'external';

export interface SetupStatus {
  isCompleted: boolean;
  hasProcessedLogs: boolean;
  needsPostgresCredentials: boolean;
  /** Whether any account exists. Null when the server could not read the account table. */
  accountExists: boolean | null;
  /** Whether the post-start recovery window is open for the main administrator. */
  mainAdminRecoveryAvailable: boolean;
  currentSetupStep: string | null;
  dataSourceChoice: string | null;
  completedPlatforms: string | null;
  mode: PostgresMode;
  postgresHost: string | null;
  postgresPort: number | null;
  postgresDatabase: string | null;
  postgresUser: string | null;
}

interface SetupStatusContextType {
  setupStatus: SetupStatus | null;
  /**
   * True once the status route has actually answered. While false, `setupStatus` may still be
   * non-null: a failed call falls back to a placeholder so the wizard gate stays closed on a
   * genuine first run, and that placeholder reads `isCompleted: false` like a real incomplete
   * setup. Anything deciding what a configured install may do must gate on THIS, not on
   * `setupStatus !== null`, or one failed request makes a working install look unconfigured.
   */
  isSetupStatusKnown: boolean;
  isLoading: boolean;
  syncError: string | null;
  refreshSetupStatus: () => Promise<void>;
  markSetupCompleted: () => void;
  clearMainAdminRecovery: () => void;
  updateWizardState: (updates: WizardStateUpdate) => Promise<boolean>;
}

export const SetupStatusContext = createContext<SetupStatusContextType | undefined>(undefined);
