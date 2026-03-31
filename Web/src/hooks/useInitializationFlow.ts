import { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useSetupStatus } from '@contexts/useSetupStatus';
import type { PicsStatus } from '@/types';

export type InitStep =
  | 'database-setup'
  | 'permissions-check'
  | 'import-historical-data'
  | 'platform-setup'
  | 'steam-api-key'
  | 'steam-auth'
  | 'depot-init'
  | 'pics-progress'
  | 'epic-auth'
  | 'log-processing'
  | 'depot-mapping';

type DataSourceChoice = 'github' | 'steam' | 'epic' | 'skip' | null;

export interface CompletedPlatforms {
  steam: 'github' | 'steam' | null;
  epic: boolean;
}

interface StepInfo {
  number: number;
  title: string;
  total: number;
}

interface UseInitializationFlowResult {
  // Current state
  currentStep: InitStep;
  stepInfo: StepInfo;
  dataSourceChoice: DataSourceChoice;
  completedPlatforms: CompletedPlatforms;

  syncError: string | null;
  isCheckingAuth: boolean;

  // Step-specific state
  picsData: PicsStatus | null;
  usingSteamAuth: boolean;
  backButtonDisabled: boolean;
  setBackButtonDisabled: (disabled: boolean) => void;

  // Navigation handlers
  handleGoBack: () => void;
  handleDatabaseSetupComplete: () => void;
  handlePermissionsCheckComplete: () => void;
  handleImportComplete: () => void;
  handleSelectPlatform: (platform: 'github' | 'steam' | 'epic') => void;
  handlePlatformContinue: () => void;
  handlePlatformSkip: () => void;
  handleEpicAuthComplete: () => void;
  handleEpicAuthSkip: () => void;
  handleSteamApiKeyComplete: () => Promise<void>;
  handleSteamAuthComplete: (usingSteam: boolean) => Promise<void>;
  handleDepotInitComplete: () => void;
  handleDepotInitGenerateOwn: () => void;
  handleDepotInitContinue: () => void;
  handlePicsProgressComplete: () => void;
  handlePicsProgressCancel: () => void;
  handleLogProcessingComplete: () => Promise<void>;
  handleLogProcessingSkip: () => Promise<void>;
  handleDepotMappingComplete: () => Promise<void>;
  handleDepotMappingSkip: () => Promise<void>;
  handleBackToSteamAuth: () => void;
}

interface UseInitializationFlowOptions {
  onInitialized: () => void;
}

const buildStepInfoMap = (
  t: (key: string) => string,
  choice: DataSourceChoice
): Record<InitStep, StepInfo> => {
  // Main flow: database-setup(1), permissions-check(2), import(3), platform-setup(4), log-processing(5), depot-mapping(6)
  // Sub-flows extend from the hub (platform-setup = step 4)
  const BASE_TOTAL = 6;

  const getSubFlowInfo = (step: InitStep): { number: number; total: number } | null => {
    switch (choice) {
      case 'github':
        if (step === 'depot-init') return { number: 4, total: BASE_TOTAL + 1 };
        break;
      case 'steam':
        if (step === 'steam-api-key') return { number: 4, total: BASE_TOTAL + 4 };
        if (step === 'steam-auth') return { number: 5, total: BASE_TOTAL + 4 };
        if (step === 'depot-init') return { number: 6, total: BASE_TOTAL + 4 };
        if (step === 'pics-progress') return { number: 7, total: BASE_TOTAL + 4 };
        break;
      case 'epic':
        if (step === 'epic-auth') return { number: 4, total: BASE_TOTAL + 1 };
        break;
    }
    return null;
  };

  const getStepInfo = (step: InitStep): { number: number; total: number } => {
    const commonSteps: Record<string, number> = {
      'database-setup': 1,
      'permissions-check': 2,
      'import-historical-data': 3,
      'platform-setup': 4,
      'log-processing': 5,
      'depot-mapping': 6
    };

    if (commonSteps[step] !== undefined) {
      return { number: commonSteps[step], total: BASE_TOTAL };
    }

    const subFlow = getSubFlowInfo(step);
    if (subFlow) return subFlow;

    // Fallback for steps not on the current path
    return { number: 4, total: BASE_TOTAL };
  };

  const steps: InitStep[] = [
    'database-setup',
    'permissions-check',
    'import-historical-data',
    'platform-setup',
    'steam-api-key',
    'steam-auth',
    'depot-init',
    'pics-progress',
    'epic-auth',
    'log-processing',
    'depot-mapping'
  ];

  const titles: Record<InitStep, string> = {
    'database-setup': t('initialization.modal.stepTitles.databaseSetup'),
    'permissions-check': t('initialization.modal.stepTitles.permissionsCheck'),
    'import-historical-data': t('initialization.modal.stepTitles.importHistoricalData'),
    'platform-setup': t('initialization.modal.stepTitles.platformSetup'),
    'steam-api-key': t('initialization.modal.stepTitles.steamApiKey'),
    'steam-auth': t('initialization.modal.stepTitles.steamPicsAuthentication'),
    'depot-init': t('initialization.modal.stepTitles.depotInitialization'),
    'pics-progress': t('initialization.modal.stepTitles.picsDataProgress'),
    'epic-auth': t('initialization.modal.stepTitles.epicAuthentication'),
    'log-processing': t('initialization.modal.stepTitles.logProcessing'),
    'depot-mapping': t('initialization.modal.stepTitles.depotMapping')
  };

  const result = {} as Record<InitStep, StepInfo>;
  for (const step of steps) {
    const info = getStepInfo(step);
    result[step] = { number: info.number, title: titles[step], total: info.total };
  }
  return result;
};

function parseCompletedPlatforms(raw: string | null): CompletedPlatforms {
  if (raw) {
    try {
      return JSON.parse(raw) as CompletedPlatforms;
    } catch {
      /* fall through */
    }
  }
  return { steam: null, epic: false };
}

function normalizeServerStep(raw: string | null): InitStep | null {
  switch (raw) {
    case 'database-setup':
    case 'permissions-check':
    case 'import-historical-data':
    case 'platform-setup':
    case 'steam-api-key':
    case 'steam-auth':
    case 'depot-init':
    case 'pics-progress':
    case 'epic-auth':
    case 'log-processing':
    case 'depot-mapping':
      return raw;
    case 'api-key':
      return 'permissions-check';
    default:
      return null;
  }
}

export function useInitializationFlow({
  onInitialized
}: UseInitializationFlowOptions): UseInitializationFlowResult {
  const { t } = useTranslation();
  const {
    setupStatus,
    isLoading: setupStatusLoading,
    syncError,
    refreshSetupStatus,
    markSetupCompleted: markSetupCompletedLocally,
    updateWizardState
  } = useSetupStatus();

  // Track whether we've done the initial hydration from server state
  const hydratedRef = useRef(false);

  // Track last-persisted values to avoid redundant API calls.
  // Without these guards, the persistence effects fire even when the value
  // hasn't changed (e.g., after setupStatus re-renders with the same data),
  // which could still produce unnecessary PATCH requests.
  const lastPersistedStep = useRef<string | null>(null);
  const lastPersistedDataSource = useRef<string | null>(null);
  const lastPersistedPlatforms = useRef<string | null>(null);

  // --- State ---
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    if (setupStatus?.currentSetupStep) {
      return setupStatus.currentSetupStep as InitStep;
    }
    return 'database-setup';
  });

  const [dataSourceChoice, setDataSourceChoice] = useState<DataSourceChoice>(() => {
    if (setupStatus?.dataSourceChoice) {
      return setupStatus.dataSourceChoice as DataSourceChoice;
    }
    return null;
  });

  const [completedPlatforms, setCompletedPlatforms] = useState<CompletedPlatforms>(() => {
    return parseCompletedPlatforms(setupStatus?.completedPlatforms ?? null);
  });

  const [picsData, setPicsData] = useState<PicsStatus | null>(null);
  const [usingSteamAuth, setUsingSteamAuth] = useState(false);
  const [backButtonDisabled, setBackButtonDisabled] = useState(false);
  const [isCheckingAuth, setIsCheckingAuth] = useState(true);
  const transitionTo = useCallback(
    (step: InitStep): InitStep => {
      if (
        step === 'database-setup' &&
        !setupStatusLoading &&
        setupStatus &&
        !setupStatus.needsPostgresCredentials
      ) {
        return 'permissions-check';
      }
      return step;
    },
    [setupStatusLoading, setupStatus]
  );

  const goToStep = useCallback(
    (step: InitStep): void => {
      setCurrentStep(transitionTo(step));
    },
    [transitionTo]
  );

  // --- Helpers ---
  const checkPicsDataStatus = useCallback(async (): Promise<PicsStatus | null> => {
    try {
      const data = await ApiService.getPicsStatus();
      setPicsData(data);
      return data;
    } catch (error) {
      console.error('Failed to check PICS data status:', error);
      return null;
    }
  }, []);

  const markSetupCompleted = useCallback(async (): Promise<boolean> => {
    try {
      await ApiService.markSetupComplete();
      markSetupCompletedLocally();
      await refreshSetupStatus();
      return true;
    } catch (error) {
      console.error('Failed to mark setup as completed:', error);
      return false;
    }
  }, [markSetupCompletedLocally, refreshSetupStatus]);

  const clearServerWizardState = useCallback(async (): Promise<boolean> => {
    return await updateWizardState({
      currentSetupStep: null,
      dataSourceChoice: null,
      completedPlatforms: null
    });
  }, [updateWizardState]);

  const handleInitializationComplete = useCallback(async (): Promise<void> => {
    const cleared = await clearServerWizardState();
    if (!cleared) {
      return;
    }
    onInitialized();
  }, [onInitialized, clearServerWizardState]);

  // --- Persist state changes to server ---
  // Each effect guards against redundant calls by comparing with the last-persisted value.
  // This prevents feedback loops even if updateWizardState or setupStatus causes re-renders.
  useEffect(() => {
    if (!hydratedRef.current) return;
    if (currentStep === lastPersistedStep.current) return;
    void (async () => {
      const persisted = await updateWizardState({ currentSetupStep: currentStep });
      if (persisted) {
        lastPersistedStep.current = currentStep;
      }
    })();
  }, [currentStep, updateWizardState]);

  useEffect(() => {
    if (!hydratedRef.current) return;
    const value = dataSourceChoice ?? null;
    if (value === lastPersistedDataSource.current) return;
    void (async () => {
      const persisted = await updateWizardState({ dataSourceChoice: value });
      if (persisted) {
        lastPersistedDataSource.current = value;
      }
    })();
  }, [dataSourceChoice, updateWizardState]);

  useEffect(() => {
    if (!hydratedRef.current) return;
    const value = JSON.stringify(completedPlatforms);
    if (value === lastPersistedPlatforms.current) return;
    void (async () => {
      const persisted = await updateWizardState({ completedPlatforms: value });
      if (persisted) {
        lastPersistedPlatforms.current = value;
      }
    })();
  }, [completedPlatforms, updateWizardState]);

  // --- Initial setup status check ---
  useEffect(() => {
    const checkSetupStatus = async (): Promise<void> => {
      try {
        // Refresh to get latest server-side wizard state
        await refreshSetupStatus();
        const setupData = await ApiService.getSetupStatus();

        if (setupData.isCompleted) {
          const cleared = await clearServerWizardState();
          if (cleared) {
            onInitialized();
          }
          return;
        }

        const serverStep = normalizeServerStep(setupData.currentSetupStep);
        if (serverStep && serverStep !== 'database-setup') {
          const serverChoice = setupData.dataSourceChoice;
          if (serverChoice) {
            setDataSourceChoice(serverChoice as DataSourceChoice);
          }

          const serverCompleted = setupData.completedPlatforms;
          if (serverCompleted) {
            setCompletedPlatforms(parseCompletedPlatforms(serverCompleted));
          }

          if (
            serverStep === 'depot-init' ||
            serverStep === 'pics-progress' ||
            serverStep === 'log-processing' ||
            serverStep === 'depot-mapping'
          ) {
            await checkPicsDataStatus();
          }
          goToStep(serverStep);
          lastPersistedStep.current = serverStep;
          lastPersistedDataSource.current = setupData.dataSourceChoice ?? null;
          lastPersistedPlatforms.current = setupData.completedPlatforms
            ? JSON.stringify(parseCompletedPlatforms(setupData.completedPlatforms))
            : JSON.stringify({ steam: null, epic: false });
          hydratedRef.current = true;
          setIsCheckingAuth(false);
          return;
        }

        // No stored step - start at the first required wizard step.
        await checkPicsDataStatus();
        goToStep('database-setup');
      } catch (error) {
        console.error('Failed to check setup status:', error);
        goToStep('database-setup');
      } finally {
        hydratedRef.current = true;
        setIsCheckingAuth(false);
      }
    };

    checkSetupStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // --- Navigation handlers ---
  const handleDatabaseSetupComplete = useCallback((): void => {
    refreshSetupStatus();
    goToStep('permissions-check');
  }, [refreshSetupStatus, goToStep]);

  const handlePermissionsCheckComplete = useCallback((): void => {
    goToStep('import-historical-data');
  }, [goToStep]);

  const handleImportComplete = useCallback((): void => {
    goToStep('platform-setup');
  }, [goToStep]);

  const handleSelectPlatform = useCallback(
    (platform: 'github' | 'steam' | 'epic'): void => {
      switch (platform) {
        case 'github':
          setDataSourceChoice('github');
          goToStep('depot-init');
          break;
        case 'steam':
          setDataSourceChoice('steam');
          goToStep('steam-api-key');
          break;
        case 'epic':
          setDataSourceChoice('epic');
          goToStep('epic-auth');
          break;
      }
    },
    [goToStep]
  );

  const handlePlatformContinue = useCallback((): void => {
    goToStep('log-processing');
  }, [goToStep]);

  const handlePlatformSkip = useCallback((): void => {
    setDataSourceChoice('skip');
    goToStep('log-processing');
  }, [goToStep]);

  const handleEpicAuthComplete = useCallback((): void => {
    setCompletedPlatforms((prev) => ({ ...prev, epic: true }));
    setDataSourceChoice(null);
    goToStep('platform-setup');
  }, [goToStep]);

  const handleEpicAuthSkip = useCallback((): void => {
    // User skipped Epic auth — return to hub without marking complete
    setDataSourceChoice(null);
    goToStep('platform-setup');
  }, [goToStep]);

  const handleSteamApiKeyComplete = useCallback(async (): Promise<void> => {
    await checkPicsDataStatus();
    goToStep('steam-auth');
  }, [checkPicsDataStatus, goToStep]);

  const handleSteamAuthComplete = useCallback(
    async (usingSteam: boolean): Promise<void> => {
      setUsingSteamAuth(usingSteam);
      await checkPicsDataStatus();
      goToStep('depot-init');
    },
    [checkPicsDataStatus, goToStep]
  );

  const handleDepotInitComplete = useCallback((): void => {
    if (dataSourceChoice === 'github') {
      setCompletedPlatforms((prev) => ({ ...prev, steam: 'github' }));
      setDataSourceChoice(null);
      goToStep('platform-setup');
    } else {
      // Steam PICS path — depot init done, go to log processing
      setCompletedPlatforms((prev) => ({ ...prev, steam: 'steam' }));
      goToStep('log-processing');
    }
  }, [dataSourceChoice, goToStep]);

  const handleDepotInitGenerateOwn = useCallback((): void => {
    goToStep('pics-progress');
  }, [goToStep]);

  const handleDepotInitContinue = useCallback((): void => {
    goToStep('pics-progress');
  }, [goToStep]);

  const handlePicsProgressComplete = useCallback((): void => {
    setCompletedPlatforms((prev) => ({ ...prev, steam: 'steam' }));
    setDataSourceChoice(null);
    goToStep('platform-setup');
  }, [goToStep]);

  const handlePicsProgressCancel = useCallback((): void => {
    setDataSourceChoice(null);
    goToStep('platform-setup');
  }, [goToStep]);

  const handleLogProcessingComplete = useCallback(async (): Promise<void> => {
    // Show depot mapping step if any platform was configured (steam or epic)
    // Note: dataSourceChoice gets reset to null after each platform completes,
    // so we check completedPlatforms instead to determine if mappings should be applied
    const hasAnyPlatform = completedPlatforms.steam !== null || completedPlatforms.epic;
    if (!hasAnyPlatform) {
      const completed = await markSetupCompleted();
      if (!completed) return;
      await handleInitializationComplete();
      return;
    }
    goToStep('depot-mapping');
  }, [completedPlatforms, markSetupCompleted, handleInitializationComplete, goToStep]);

  const handleLogProcessingSkip = useCallback(async (): Promise<void> => {
    const completed = await markSetupCompleted();
    if (!completed) return;
    await handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleDepotMappingComplete = useCallback(async (): Promise<void> => {
    const completed = await markSetupCompleted();
    if (!completed) return;
    await handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleDepotMappingSkip = useCallback(async (): Promise<void> => {
    const completed = await markSetupCompleted();
    if (!completed) return;
    await handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleBackToSteamAuth = useCallback((): void => {
    setUsingSteamAuth(false);
    goToStep('steam-auth');
  }, [goToStep]);

  const handleGoBack = useCallback((): void => {
    switch (currentStep) {
      case 'permissions-check':
        // Intentionally bypass transition guards so "Back" does not bounce forward.
        setCurrentStep('database-setup');
        break;
      case 'import-historical-data':
        goToStep('permissions-check');
        break;
      case 'platform-setup':
        goToStep('import-historical-data');
        break;
      case 'steam-api-key':
        goToStep('platform-setup');
        break;
      case 'steam-auth':
        goToStep('steam-api-key');
        break;
      case 'epic-auth':
        goToStep('platform-setup');
        break;
      case 'depot-init':
        if (dataSourceChoice === 'steam') {
          goToStep('steam-auth');
        } else {
          goToStep('platform-setup');
        }
        break;
      case 'pics-progress':
        goToStep('depot-init');
        break;
      case 'log-processing':
        goToStep('platform-setup');
        break;
      case 'depot-mapping':
        goToStep('log-processing');
        break;
      default:
        break;
    }
  }, [currentStep, dataSourceChoice, goToStep]);

  // --- Computed ---
  const stepInfo = buildStepInfoMap(t, dataSourceChoice)[currentStep];

  return {
    currentStep,
    stepInfo,
    dataSourceChoice,
    completedPlatforms,

    isCheckingAuth: isCheckingAuth || setupStatusLoading,
    picsData,
    usingSteamAuth,
    backButtonDisabled,
    setBackButtonDisabled,
    syncError,

    handleGoBack,
    handleDatabaseSetupComplete,
    handlePermissionsCheckComplete,
    handleImportComplete,
    handleSelectPlatform,
    handlePlatformContinue,
    handlePlatformSkip,
    handleEpicAuthComplete,
    handleEpicAuthSkip,
    handleSteamApiKeyComplete,
    handleSteamAuthComplete,
    handleDepotInitComplete,
    handleDepotInitGenerateOwn,
    handleDepotInitContinue,
    handlePicsProgressComplete,
    handlePicsProgressCancel,
    handleLogProcessingComplete,
    handleLogProcessingSkip,
    handleDepotMappingComplete,
    handleDepotMappingSkip,
    handleBackToSteamAuth
  };
}
