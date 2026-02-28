import React, { useState, useEffect } from 'react';
import { Rocket, ArrowLeft } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';
import { useInitializationAuth } from '@hooks/useInitializationAuth';
import {
  ApiKeyStep,
  PermissionsCheckStep,
  ImportHistoricalDataStep,
  DataSourceChoiceStep,
  SteamApiKeyStep,
  SteamPicsAuthStep,
  DepotInitStep,
  PicsProgressStep,
  LogProcessingStep,
  DepotMappingStep
} from '../../initialization/steps';

interface DepotInitializationModalProps {
  onInitialized: () => void;
  onAuthChanged?: () => void;
}

/** PICS data status from the API */
interface PicsStatus {
  jsonFile?: {
    exists: boolean;
    totalMappings?: number;
  };
  database?: {
    totalMappings?: number;
  };
  steamKit2?: {
    isReady: boolean;
    isRebuildRunning?: boolean;
  };
}

type InitStep =
  | 'api-key'
  | 'permissions-check'
  | 'import-historical-data'
  | 'data-source-choice'
  | 'steam-api-key'
  | 'steam-auth'
  | 'depot-init'
  | 'pics-progress'
  | 'log-processing'
  | 'depot-mapping';

const getStepInfo = (
  t: (key: string) => string
): Record<InitStep, { number: number; title: string; total: number }> => ({
  'api-key': { number: 1, title: t('initialization.modal.stepTitles.authentication'), total: 10 },
  'permissions-check': {
    number: 2,
    title: t('initialization.modal.stepTitles.permissionsCheck'),
    total: 10
  },
  'import-historical-data': {
    number: 3,
    title: t('initialization.modal.stepTitles.importHistoricalData'),
    total: 10
  },
  'data-source-choice': {
    number: 4,
    title: t('initialization.modal.stepTitles.dataSourceSelection'),
    total: 10
  },
  'steam-api-key': {
    number: 5,
    title: t('initialization.modal.stepTitles.steamApiKey'),
    total: 10
  },
  'steam-auth': {
    number: 6,
    title: t('initialization.modal.stepTitles.steamPicsAuthentication'),
    total: 10
  },
  'depot-init': {
    number: 7,
    title: t('initialization.modal.stepTitles.depotInitialization'),
    total: 10
  },
  'pics-progress': {
    number: 8,
    title: t('initialization.modal.stepTitles.picsDataProgress'),
    total: 10
  },
  'log-processing': {
    number: 9,
    title: t('initialization.modal.stepTitles.logProcessing'),
    total: 10
  },
  'depot-mapping': {
    number: 10,
    title: t('initialization.modal.stepTitles.depotMapping'),
    total: 10
  }
});

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  onAuthChanged
}) => {
  const { t } = useTranslation();
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    const stored = storage.getItem('initializationCurrentStep');
    return (stored as InitStep) || 'api-key';
  });

  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);
  const [picsData, setPicsData] = useState<PicsStatus | null>(null);
  const [usingSteamAuth, setUsingSteamAuth] = useState<boolean>(false);
  const [dataSourceChoice, setDataSourceChoice] = useState<'github' | 'steam' | null>(() => {
    const stored = storage.getItem('dataSourceChoice');
    return (stored as 'github' | 'steam') || null;
  });
  const [authDisabled, setAuthDisabled] = useState<boolean>(false);
  const [backButtonDisabled, setBackButtonDisabled] = useState<boolean>(false);
  const [isCheckingAuth, setIsCheckingAuth] = useState<boolean>(true);

  // Helper functions (defined before hook and useEffects that depend on them)
  const clearAllLocalStorage = () => {
    storage.removeItem('initializationCurrentStep');
    storage.removeItem('dataSourceChoice');
    storage.removeItem('steamApiKey');
    storage.removeItem('importConnectionString');
    storage.removeItem('importBatchSize');
    storage.removeItem('importOverwriteExisting');
    storage.removeItem('initializationVersion');
  };

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      const setupResponse = await fetch(
        '/api/system/setup',
        ApiService.getFetchOptions({ cache: 'no-store' })
      );
      if (setupResponse.ok) {
        const setupData = await setupResponse.json();
        const hasData = setupData.isSetupCompleted || setupData.hasProcessedLogs || false;
        setDataAvailable(hasData);
        return hasData;
      }
      setDataAvailable(false);
      return false;
    } catch (error) {
      console.error('Failed to check data availability:', error);
      setDataAvailable(false);
      return false;
    } finally {
      setCheckingDataAvailability(false);
    }
  };

  const checkPicsDataStatus = async () => {
    try {
      const data = await ApiService.getPicsStatus();
      setPicsData(data);
      return data;
    } catch (error) {
      console.error('Failed to check PICS data status:', error);
      return null;
    }
  };

  const markSetupCompleted = async () => {
    try {
      await fetch(
        '/api/system/setup',
        ApiService.getFetchOptions({
          cache: 'no-store',
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ completed: true })
        })
      );
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
    }
  };

  const handleInitializationComplete = () => {
    clearAllLocalStorage();
    onInitialized();
  };

  // Consolidated authentication handler for all auth modes
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

  // Effects
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
    const checkSetupStatus = async () => {
      const INIT_VERSION = '1.0';
      const storedVersion = storage.getItem('initializationVersion');

      if (storedVersion !== INIT_VERSION) {
        clearAllLocalStorage();
        storage.setItem('initializationVersion', INIT_VERSION);
      }

      await checkDataAvailability();

      try {
        const authCheck = await authService.checkAuth();
        // Auth is always available (either admin or guest sessions)
        // If not authenticated, user needs to authenticate or start guest session
        setAuthDisabled(false);

        const setupResponse = await fetch(
          '/api/system/setup',
          ApiService.getFetchOptions({ cache: 'no-store' })
        );
        const setupData = await setupResponse.json();

        // Setup complete and authenticated → go to app
        if (setupData.isCompleted && authCheck.isAuthenticated) {
          clearAllLocalStorage();
          onInitialized();
          return;
        }

        // Not authenticated → show api-key step
        // Don't clear localStorage here - just reset to api-key step
        // This preserves the initialization flow state in App.tsx
        if (!authCheck.isAuthenticated) {
          setCurrentStep('api-key');
          setIsCheckingAuth(false);
          return;
        }

        // Authenticated but setup not complete → continue from stored step
        const storedStep = storage.getItem('initializationCurrentStep');
        if (storedStep && storedStep !== 'api-key') {
          const storedChoice = storage.getItem('dataSourceChoice');
          if (storedChoice) {
            setDataSourceChoice(storedChoice as 'github' | 'steam');
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
          // Already authenticated → go to permissions check
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

  const handlePermissionsCheckComplete = () => {
    setCurrentStep('import-historical-data');
  };

  const handleImportComplete = () => {
    setCurrentStep('data-source-choice');
  };

  const handleChooseGithub = () => {
    setDataSourceChoice('github');
    setCurrentStep('depot-init');
  };

  const handleChooseSteam = () => {
    setDataSourceChoice('steam');
    setCurrentStep('steam-api-key');
  };

  const handleSteamApiKeyComplete = async () => {
    await checkPicsDataStatus();
    setCurrentStep('steam-auth');
  };

  const handleSteamAuthComplete = async (usingSteam: boolean) => {
    setUsingSteamAuth(usingSteam);
    await checkPicsDataStatus();
    setCurrentStep('depot-init');
  };

  const handleDepotInitComplete = () => {
    setCurrentStep('log-processing');
  };

  const handleDepotInitGenerateOwn = () => {
    setCurrentStep('pics-progress');
  };

  const handleDepotInitContinue = () => {
    setCurrentStep('pics-progress');
  };

  const handlePicsProgressComplete = () => {
    setCurrentStep('log-processing');
  };

  const handlePicsProgressCancel = () => {
    setDataSourceChoice(null);
    setCurrentStep('data-source-choice');
  };

  const handleLogProcessingComplete = () => {
    setCurrentStep('depot-mapping');
  };

  const handleLogProcessingSkip = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  const handleDepotMappingComplete = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  const handleDepotMappingSkip = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  const handleGoBack = () => {
    switch (currentStep) {
      case 'permissions-check':
        setCurrentStep('api-key');
        break;
      case 'import-historical-data':
        setCurrentStep('permissions-check');
        break;
      case 'data-source-choice':
        setCurrentStep('import-historical-data');
        break;
      case 'steam-api-key':
        setCurrentStep('data-source-choice');
        break;
      case 'steam-auth':
        setCurrentStep(dataSourceChoice === 'steam' ? 'steam-api-key' : 'data-source-choice');
        break;
      case 'depot-init':
        if (dataSourceChoice === 'steam') {
          setCurrentStep('steam-auth');
        } else {
          setCurrentStep('data-source-choice');
        }
        break;
      case 'pics-progress':
        setCurrentStep('depot-init');
        break;
      case 'log-processing':
        setCurrentStep('depot-init');
        break;
      case 'depot-mapping':
        setCurrentStep('log-processing');
        break;
      default:
        break;
    }
  };

  const renderStep = () => {
    // Show loading state while checking auth for steps that make API calls
    if (isCheckingAuth && currentStep !== 'api-key') {
      return (
        <div className="flex flex-col items-center justify-center py-12">
          <div className="w-12 h-12 rounded-full border-4 border-themed-secondary border-t-primary animate-spin mb-4" />
          <p className="text-themed-secondary">{t('common.loading', 'Loading...')}</p>
        </div>
      );
    }

    switch (currentStep) {
      case 'api-key':
        return (
          <ApiKeyStep
            apiKey={apiKey}
            setApiKey={setApiKey}
            authenticating={authenticating}
            authError={authError}
            dataAvailable={dataAvailable}
            checkingDataAvailability={checkingDataAvailability}
            authDisabled={authDisabled}
            onAuthenticate={() => authenticate('apiKey')}
            onStartGuestMode={() => authenticate('guest')}
            onContinueAsAdmin={() => authenticate('admin')}
          />
        );

      case 'permissions-check':
        return <PermissionsCheckStep onComplete={handlePermissionsCheckComplete} />;

      case 'import-historical-data':
        return (
          <ImportHistoricalDataStep
            onComplete={handleImportComplete}
            onSkip={handleImportComplete}
          />
        );

      case 'data-source-choice':
        return (
          <DataSourceChoiceStep
            onChooseGithub={handleChooseGithub}
            onChooseSteam={handleChooseSteam}
          />
        );

      case 'steam-api-key':
        return <SteamApiKeyStep onComplete={handleSteamApiKeyComplete} />;

      case 'steam-auth':
        return <SteamPicsAuthStep onComplete={handleSteamAuthComplete} />;

      case 'depot-init':
        return (
          <DepotInitStep
            picsData={picsData}
            usingSteamAuth={usingSteamAuth}
            hideOptions={dataSourceChoice === 'github'}
            onDownloadPrecreated={handleDepotInitComplete}
            onGenerateOwn={handleDepotInitGenerateOwn}
            onContinue={handleDepotInitContinue}
            onComplete={handleDepotInitComplete}
            onBackToSteamAuth={() => {
              setUsingSteamAuth(false);
              setCurrentStep('steam-auth');
            }}
          />
        );

      case 'pics-progress':
        return (
          <PicsProgressStep
            onComplete={handlePicsProgressComplete}
            onProcessingStateChange={setBackButtonDisabled}
            onCancel={handlePicsProgressCancel}
          />
        );

      case 'log-processing':
        return (
          <LogProcessingStep
            onComplete={handleLogProcessingComplete}
            onSkip={handleLogProcessingSkip}
            onProcessingStateChange={setBackButtonDisabled}
          />
        );

      case 'depot-mapping':
        return (
          <DepotMappingStep
            onComplete={handleDepotMappingComplete}
            onSkip={handleDepotMappingSkip}
          />
        );

      default:
        return null;
    }
  };

  const stepInfo = getStepInfo(t)[currentStep];

  return (
    <div className="fixed inset-0 z-[80] flex items-center justify-center p-4 bg-themed-primary">
      {/* Stripe background pattern */}
      <div
        className="absolute inset-0 opacity-5"
        style={{
          backgroundImage: `repeating-linear-gradient(45deg, transparent, transparent 35px, var(--theme-text-primary) 35px, var(--theme-text-primary) 70px)`
        }}
      />

      {/* Main Card */}
      <div className="relative z-10 w-full max-w-4xl rounded-xl border overflow-hidden flex flex-col bg-themed-secondary border-themed-primary max-h-[min(calc(100vh-2rem),800px)]">
        {/* Header */}
        <div className="px-8 py-5 border-b flex items-center justify-between border-themed-secondary">
          <div className="flex items-center gap-3">
            {currentStep !== 'api-key' && (
              <button
                onClick={backButtonDisabled ? undefined : handleGoBack}
                disabled={backButtonDisabled}
                className={`p-1.5 rounded-lg transition-colors bg-transparent ${
                  backButtonDisabled
                    ? 'text-themed-muted cursor-not-allowed opacity-50'
                    : 'text-themed-secondary cursor-pointer'
                }`}
                title={
                  backButtonDisabled
                    ? t('initialization.modal.cannotGoBack')
                    : t('initialization.modal.goBack')
                }
              >
                <ArrowLeft className="w-5 h-5" />
              </button>
            )}
            <div className="flex items-center gap-2">
              <Rocket className="w-5 h-5 text-primary" />
              <span className="font-semibold text-themed-primary">
                {t('initialization.modal.setupWizard')}
              </span>
            </div>
          </div>
          <div className="text-xs font-medium px-2.5 py-1 rounded-full bg-themed-tertiary text-themed-secondary">
            {stepInfo.number} / {stepInfo.total}
          </div>
        </div>

        {/* Progress Bar */}
        <div className="h-1 bg-themed-tertiary">
          <div
            className="h-full transition-all duration-300 ease-out bg-primary"
            style={{ width: `${(stepInfo.number / stepInfo.total) * 100}%` }}
          />
        </div>

        {/* Content */}
        <div className="p-8 overflow-y-auto min-h-0">{renderStep()}</div>
      </div>
    </div>
  );
};

export default DepotInitializationModal;
