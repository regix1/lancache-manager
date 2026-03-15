import { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';
import { useInitializationAuth } from '@hooks/useInitializationAuth';
import type { PicsStatus } from '@/types';

export type InitStep =
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

const INIT_VERSION = '1.0';

const buildStepInfoMap = (
  t: (key: string) => string,
  choice: DataSourceChoice
): Record<InitStep, StepInfo> => {
  // Main flow: api-key(1), permissions-check(2), import(3), platform-setup(4), log-processing(5), depot-mapping(6)
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
      'api-key': 1,
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

const clearAllLocalStorage = (): void => {
  storage.removeItem('initializationCurrentStep');
  storage.removeItem('dataSourceChoice');
  storage.removeItem('completedPlatforms');
  storage.removeItem('steamApiKey');
  storage.removeItem('importConnectionString');
  storage.removeItem('importBatchSize');
  storage.removeItem('importOverwriteExisting');
  storage.removeItem('initializationVersion');
};

export function useInitializationFlow({
  onInitialized,
  onAuthChanged
}: UseInitializationFlowOptions): UseInitializationFlowResult {
  const { t } = useTranslation();

  // --- State ---
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    const stored = storage.getItem('initializationCurrentStep');
    return (stored as InitStep) || 'api-key';
  });

  const [dataSourceChoice, setDataSourceChoice] = useState<DataSourceChoice>(() => {
    const stored = storage.getItem('dataSourceChoice');
    return (stored as DataSourceChoice) || null;
  });

  const [completedPlatforms, setCompletedPlatforms] = useState<CompletedPlatforms>(() => {
    const stored = storage.getItem('completedPlatforms');
    if (stored) {
      try {
        return JSON.parse(stored) as CompletedPlatforms;
      } catch {
        /* fall through */
      }
    }
    return { steam: null, epic: false };
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

  const handleInitializationComplete = useCallback((): void => {
    clearAllLocalStorage();
    onInitialized();
  }, [onInitialized]);

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

  // --- Effects ---
  useEffect(() => {
    storage.setItem('initializationCurrentStep', currentStep);
  }, [currentStep]);

  useEffect(() => {
    if (dataSourceChoice) {
      storage.setItem('dataSourceChoice', dataSourceChoice);
    } else {
      storage.removeItem('dataSourceChoice');
    }
  }, [dataSourceChoice]);

  useEffect(() => {
    storage.setItem('completedPlatforms', JSON.stringify(completedPlatforms));
  }, [completedPlatforms]);

  useEffect(() => {
    const checkSetupStatus = async (): Promise<void> => {
      const storedVersion = storage.getItem('initializationVersion');

      if (storedVersion !== INIT_VERSION) {
        clearAllLocalStorage();
        storage.setItem('initializationVersion', INIT_VERSION);
      }

      await checkDataAvailability();

      try {
        const authCheck = await authService.checkAuth();
        // Auth is always available (either admin or guest sessions)
        setAuthDisabled(false);

        const setupData = await ApiService.getSetupStatus();

        // Setup complete and authenticated -> go to app
        if (setupData.isCompleted && authCheck.isAuthenticated) {
          clearAllLocalStorage();
          onInitialized();
          return;
        }

        // Not authenticated -> show api-key step
        if (!authCheck.isAuthenticated) {
          setCurrentStep('api-key');
          setIsCheckingAuth(false);
          return;
        }

        // Authenticated but setup not complete -> continue from stored step
        const storedStep = storage.getItem('initializationCurrentStep');
        if (storedStep && storedStep !== 'api-key') {
          const storedChoice = storage.getItem('dataSourceChoice');
          if (storedChoice) {
            setDataSourceChoice(storedChoice as DataSourceChoice);
          }

          const storedCompleted = storage.getItem('completedPlatforms');
          if (storedCompleted) {
            try {
              setCompletedPlatforms(JSON.parse(storedCompleted) as CompletedPlatforms);
            } catch {
              /* ignore parse errors */
            }
          }

          if (
            storedStep === 'depot-init' ||
            storedStep === 'pics-progress' ||
            storedStep === 'log-processing' ||
            storedStep === 'depot-mapping'
          ) {
            await checkPicsDataStatus();
          }
          setCurrentStep(storedStep as InitStep);
          setIsCheckingAuth(false);
          return;
        }

        // No stored step or at api-key step
        if (authCheck.isAuthenticated) {
          await checkPicsDataStatus();
          setCurrentStep('permissions-check');
        } else {
          setCurrentStep('api-key');
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setCurrentStep('api-key');
      } finally {
        setIsCheckingAuth(false);
      }
    };

    checkSetupStatus();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // --- Navigation handlers ---
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
      case 'permissions-check':
        setCurrentStep('api-key');
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
