import { useState, useEffect, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { useInitializationAuth } from '@hooks/useInitializationAuth';
import { useSetupStatus } from '@contexts/useSetupStatus';
import type { PicsStatus } from '@/types';

export type InitStep =
  | 'database-setup'
  | 'api-key'
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

  // Auth state
  apiKey: string;
  setApiKey: (key: string) => void;
  authenticating: boolean;
  authError: string | null;
  authDisabled: boolean;
  dataAvailable: boolean;
  checkingDataAvailability: boolean;
  isCheckingAuth: boolean;
  authenticate: (mode: 'apiKey' | 'guest' | 'admin') => Promise<void>;

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
  handleLogProcessingComplete: () => void;
  handleLogProcessingSkip: () => Promise<void>;
  handleDepotMappingComplete: () => Promise<void>;
  handleDepotMappingSkip: () => Promise<void>;
  handleBackToSteamAuth: () => void;
}

interface UseInitializationFlowOptions {
  onInitialized: () => void;
  onAuthChanged?: () => void;
}

const buildStepInfoMap = (
  t: (key: string) => string,
  choice: DataSourceChoice
): Record<InitStep, StepInfo> => {
  // Main flow: database-setup(1), permissions-check(2), import(3), platform-setup(4), log-processing(5), depot-mapping(6)
  // api-key step is skipped since auth modal handles authentication before the wizard starts
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
      'api-key': 1, // fallback only — normally skipped
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
    'api-key',
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
    'api-key': t('initialization.modal.stepTitles.authentication'),
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

export function useInitializationFlow({
  onInitialized,
  onAuthChanged
}: UseInitializationFlowOptions): UseInitializationFlowResult {
  const { t } = useTranslation();
  const {
    setupStatus,
    isLoading: setupStatusLoading,
    refreshSetupStatus,
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

  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);
  const [picsData, setPicsData] = useState<PicsStatus | null>(null);
  const [usingSteamAuth, setUsingSteamAuth] = useState(false);
  const [authDisabled, setAuthDisabled] = useState(false);
  const [backButtonDisabled, setBackButtonDisabled] = useState(false);
  const [isCheckingAuth, setIsCheckingAuth] = useState(true);

  // --- Helpers ---
  const checkDataAvailability = useCallback(async (): Promise<boolean> => {
    setCheckingDataAvailability(true);
    try {
      const setupData = await ApiService.getSetupStatus();
      const hasData =
        setupData.isCompleted || setupData.setupCompleted || setupData.hasProcessedLogs || false;
      setDataAvailable(hasData);
      return hasData;
    } catch (error) {
      console.error('Failed to check data availability:', error);
      setDataAvailable(false);
      return false;
    } finally {
      setCheckingDataAvailability(false);
    }
  }, []);

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

  const markSetupCompleted = useCallback(async (): Promise<void> => {
    try {
      await ApiService.markSetupComplete();
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
    }
  }, []);

  const clearServerWizardState = useCallback(async (): Promise<void> => {
    try {
      await updateWizardState({
        currentSetupStep: null,
        dataSourceChoice: null,
        completedPlatforms: null
      });
    } catch (error) {
      console.warn('Failed to clear wizard state on server:', error);
    }
  }, [updateWizardState]);

  const handleInitializationComplete = useCallback((): void => {
    clearServerWizardState();
    onInitialized();
  }, [onInitialized, clearServerWizardState]);

  // --- Auth hook integration ---
  const { authenticate } = useInitializationAuth({
    apiKey,
    setAuthError,
    setAuthenticating,
    onAuthChanged,
    checkPicsDataStatus,
    checkDataAvailability,
    setCurrentStep,
    onInitializationComplete: handleInitializationComplete
  });

  // --- Persist state changes to server ---
  // Each effect guards against redundant calls by comparing with the last-persisted value.
  // This prevents feedback loops even if updateWizardState or setupStatus causes re-renders.
  useEffect(() => {
    if (!hydratedRef.current) return;
    if (currentStep === lastPersistedStep.current) return;
    lastPersistedStep.current = currentStep;
    updateWizardState({ currentSetupStep: currentStep });
  }, [currentStep, updateWizardState]);

  useEffect(() => {
    if (!hydratedRef.current) return;
    const value = dataSourceChoice ?? null;
    if (value === lastPersistedDataSource.current) return;
    lastPersistedDataSource.current = value;
    updateWizardState({ dataSourceChoice: value });
  }, [dataSourceChoice, updateWizardState]);

  useEffect(() => {
    if (!hydratedRef.current) return;
    const value = JSON.stringify(completedPlatforms);
    if (value === lastPersistedPlatforms.current) return;
    lastPersistedPlatforms.current = value;
    updateWizardState({ completedPlatforms: value });
  }, [completedPlatforms, updateWizardState]);

  // --- Initial setup status check ---
  useEffect(() => {
    const checkSetupStatus = async (): Promise<void> => {
      await checkDataAvailability();

      try {
        const authCheck = await authService.checkAuth();
        // Auth is always available (either admin or guest sessions)
        setAuthDisabled(false);

        // Refresh to get latest server-side wizard state
        await refreshSetupStatus();
        const setupData = await ApiService.getSetupStatus();

        // Setup complete and authenticated -> go to app
        if (setupData.isCompleted && authCheck.isAuthenticated) {
          clearServerWizardState();
          onInitialized();
          return;
        }

        // Not authenticated -> show database-setup step (will auto-skip if not needed)
        if (!authCheck.isAuthenticated) {
          setCurrentStep('database-setup');
          lastPersistedStep.current = 'database-setup';
          lastPersistedDataSource.current = null;
          lastPersistedPlatforms.current = JSON.stringify({ steam: null, epic: false });
          hydratedRef.current = true;
          setIsCheckingAuth(false);
          return;
        }

        // Authenticated but setup not complete -> restore from server state
        const serverStep = setupStatus?.currentSetupStep;
        if (serverStep && serverStep !== 'database-setup' && serverStep !== 'api-key') {
          const serverChoice = setupStatus?.dataSourceChoice;
          if (serverChoice) {
            setDataSourceChoice(serverChoice as DataSourceChoice);
          }

          const serverCompleted = setupStatus?.completedPlatforms;
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
          setCurrentStep(serverStep as InitStep);
          lastPersistedStep.current = serverStep;
          lastPersistedDataSource.current = setupStatus?.dataSourceChoice ?? null;
          lastPersistedPlatforms.current = setupStatus?.completedPlatforms
            ? JSON.stringify(parseCompletedPlatforms(setupStatus.completedPlatforms))
            : JSON.stringify({ steam: null, epic: false });
          hydratedRef.current = true;
          setIsCheckingAuth(false);
          return;
        }

        // No stored step or at api-key/database-setup step
        if (authCheck.isAuthenticated) {
          await checkPicsDataStatus();
          setCurrentStep('permissions-check');
        } else {
          setCurrentStep('database-setup');
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setCurrentStep('database-setup');
      } finally {
        hydratedRef.current = true;
        setIsCheckingAuth(false);
      }
    };

    checkSetupStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // --- Auto-skip database-setup if not needed ---
  useEffect(() => {
    if (currentStep !== 'database-setup') return;

    // Wait until setupStatus is definitively loaded from the server.
    // Without this guard, setupStatus could be populated from error/fallback
    // defaults (which set needsPostgresCredentials: false), causing the step
    // to be skipped before the real server response arrives.
    if (setupStatusLoading) return;

    // Use setupStatus from context — needsPostgresCredentials drives this
    if (setupStatus && !setupStatus.needsPostgresCredentials) {
      // Database already configured, skip to permissions-check
      // (api-key step is skipped — auth modal already handled authentication)
      setCurrentStep('permissions-check');
    }
  }, [currentStep, setupStatus, setupStatusLoading]);

  // --- Navigation handlers ---
  const handleDatabaseSetupComplete = useCallback((): void => {
    refreshSetupStatus();
    // Skip api-key step — auth modal already handled authentication
    setCurrentStep('permissions-check');
  }, [refreshSetupStatus]);

  const handlePermissionsCheckComplete = useCallback((): void => {
    setCurrentStep('import-historical-data');
  }, []);

  const handleImportComplete = useCallback((): void => {
    setCurrentStep('platform-setup');
  }, []);

  const handleSelectPlatform = useCallback((platform: 'github' | 'steam' | 'epic'): void => {
    switch (platform) {
      case 'github':
        setDataSourceChoice('github');
        setCurrentStep('depot-init');
        break;
      case 'steam':
        setDataSourceChoice('steam');
        setCurrentStep('steam-api-key');
        break;
      case 'epic':
        setDataSourceChoice('epic');
        setCurrentStep('epic-auth');
        break;
    }
  }, []);

  const handlePlatformContinue = useCallback((): void => {
    setCurrentStep('log-processing');
  }, []);

  const handlePlatformSkip = useCallback((): void => {
    setDataSourceChoice('skip');
    setCurrentStep('log-processing');
  }, []);

  const handleEpicAuthComplete = useCallback((): void => {
    setCompletedPlatforms((prev) => ({ ...prev, epic: true }));
    setCurrentStep('platform-setup');
  }, []);

  const handleEpicAuthSkip = useCallback((): void => {
    // User skipped Epic auth — return to hub without marking complete
    setCurrentStep('platform-setup');
  }, []);

  const handleSteamApiKeyComplete = useCallback(async (): Promise<void> => {
    await checkPicsDataStatus();
    setCurrentStep('steam-auth');
  }, [checkPicsDataStatus]);

  const handleSteamAuthComplete = useCallback(
    async (usingSteam: boolean): Promise<void> => {
      setUsingSteamAuth(usingSteam);
      await checkPicsDataStatus();
      setCurrentStep('depot-init');
    },
    [checkPicsDataStatus]
  );

  const handleDepotInitComplete = useCallback((): void => {
    if (dataSourceChoice === 'github') {
      setCompletedPlatforms((prev) => ({ ...prev, steam: 'github' }));
      setCurrentStep('platform-setup');
    } else {
      // Steam PICS path — depot init done, go to log processing
      setCurrentStep('log-processing');
    }
  }, [dataSourceChoice]);

  const handleDepotInitGenerateOwn = useCallback((): void => {
    setCurrentStep('pics-progress');
  }, []);

  const handleDepotInitContinue = useCallback((): void => {
    setCurrentStep('pics-progress');
  }, []);

  const handlePicsProgressComplete = useCallback((): void => {
    setCompletedPlatforms((prev) => ({ ...prev, steam: 'steam' }));
    setCurrentStep('platform-setup');
  }, []);

  const handlePicsProgressCancel = useCallback((): void => {
    setDataSourceChoice(null);
    setCurrentStep('platform-setup');
  }, []);

  const handleLogProcessingComplete = useCallback((): void => {
    setCurrentStep('depot-mapping');
  }, []);

  const handleLogProcessingSkip = useCallback(async (): Promise<void> => {
    await markSetupCompleted();
    handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleDepotMappingComplete = useCallback(async (): Promise<void> => {
    await markSetupCompleted();
    handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleDepotMappingSkip = useCallback(async (): Promise<void> => {
    await markSetupCompleted();
    handleInitializationComplete();
  }, [markSetupCompleted, handleInitializationComplete]);

  const handleBackToSteamAuth = useCallback((): void => {
    setUsingSteamAuth(false);
    setCurrentStep('steam-auth');
  }, []);

  const handleGoBack = useCallback((): void => {
    switch (currentStep) {
      case 'api-key':
        setCurrentStep('database-setup');
        break;
      case 'permissions-check':
        // Skip back over api-key — auth modal handles authentication
        setCurrentStep('database-setup');
        break;
      case 'import-historical-data':
        setCurrentStep('permissions-check');
        break;
      case 'platform-setup':
        setCurrentStep('import-historical-data');
        break;
      case 'steam-api-key':
        setCurrentStep('platform-setup');
        break;
      case 'steam-auth':
        setCurrentStep('steam-api-key');
        break;
      case 'epic-auth':
        setCurrentStep('platform-setup');
        break;
      case 'depot-init':
        if (dataSourceChoice === 'steam') {
          setCurrentStep('steam-auth');
        } else {
          setCurrentStep('platform-setup');
        }
        break;
      case 'pics-progress':
        setCurrentStep('depot-init');
        break;
      case 'log-processing':
        setCurrentStep('platform-setup');
        break;
      case 'depot-mapping':
        setCurrentStep('log-processing');
        break;
      default:
        break;
    }
  }, [currentStep, dataSourceChoice]);

  // --- Computed ---
  const stepInfo = buildStepInfoMap(t, dataSourceChoice)[currentStep];

  return {
    currentStep,
    stepInfo,
    dataSourceChoice,
    completedPlatforms,

    apiKey,
    setApiKey,
    authenticating,
    authError,
    authDisabled,
    dataAvailable,
    checkingDataAvailability,
    isCheckingAuth,
    authenticate,

    picsData,
    usingSteamAuth,
    backButtonDisabled,
    setBackButtonDisabled,

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
