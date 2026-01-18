import React, { useState, useEffect } from 'react';
import { Rocket, ArrowLeft } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';
import {
  ApiKeyStep,
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
  | 'import-historical-data'
  | 'data-source-choice'
  | 'steam-api-key'
  | 'steam-auth'
  | 'depot-init'
  | 'pics-progress'
  | 'log-processing'
  | 'depot-mapping';

const getStepInfo = (t: (key: string) => string): Record<InitStep, { number: number; title: string; total: number }> => ({
  'api-key': { number: 1, title: t('initialization.modal.stepTitles.authentication'), total: 9 },
  'import-historical-data': { number: 2, title: t('initialization.modal.stepTitles.importHistoricalData'), total: 9 },
  'data-source-choice': { number: 3, title: t('initialization.modal.stepTitles.dataSourceSelection'), total: 9 },
  'steam-api-key': { number: 4, title: t('initialization.modal.stepTitles.steamApiKey'), total: 9 },
  'steam-auth': { number: 5, title: t('initialization.modal.stepTitles.steamPicsAuthentication'), total: 9 },
  'depot-init': { number: 6, title: t('initialization.modal.stepTitles.depotInitialization'), total: 9 },
  'pics-progress': { number: 7, title: t('initialization.modal.stepTitles.picsDataProgress'), total: 9 },
  'log-processing': { number: 8, title: t('initialization.modal.stepTitles.logProcessing'), total: 9 },
  'depot-mapping': { number: 9, title: t('initialization.modal.stepTitles.depotMapping'), total: 9 }
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
        const authRequired = authCheck.requiresAuth;
        setAuthDisabled(!authRequired);

        const setupResponse = await fetch('/api/system/setup', ApiService.getFetchOptions());
        const setupData = await setupResponse.json();

        if (setupData.isCompleted && authCheck.isAuthenticated) {
          clearAllLocalStorage();
          onInitialized();
          return;
        }

        const storedStep = storage.getItem('initializationCurrentStep');
        if (storedStep) {
          if (!authCheck.isAuthenticated && !setupData.isCompleted) {
            clearAllLocalStorage();
            setCurrentStep('api-key');
            return;
          }

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
          return;
        }

        if (!authRequired || !authCheck.isAuthenticated) {
          setCurrentStep('api-key');
        } else {
          await checkPicsDataStatus();
          setCurrentStep('import-historical-data');
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setCurrentStep('api-key');
      }
    };

    checkSetupStatus();
  }, []);

  const clearAllLocalStorage = () => {
    storage.removeItem('initializationCurrentStep');
    storage.removeItem('dataSourceChoice');
    storage.removeItem('initializationApiKey');
    storage.removeItem('steamApiKey');
    storage.removeItem('importConnectionString');
    storage.removeItem('importBatchSize');
    storage.removeItem('importOverwriteExisting');
    storage.removeItem('initializationVersion');
  };

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      const setupResponse = await fetch('/api/system/setup', ApiService.getFetchOptions());
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
      await fetch('/api/system/setup', ApiService.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ completed: true })
      }));
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
    }
  };

  const handleInitializationComplete = () => {
    clearAllLocalStorage();
    onInitialized();
  };

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError('API key is required');
      return;
    }

    setAuthenticating(true);
    setAuthError(null);

    try {
      const result = await authService.register(apiKey, null);
      if (result.success) {
        const authCheck = await authService.checkAuth();
        if (authCheck.isAuthenticated) {
          onAuthChanged?.();
          await checkPicsDataStatus();
          setCurrentStep('import-historical-data');
        } else {
          setAuthError(t('modals.auth.errors.verificationFailed'));
        }
      } else {
        setAuthError(result.message);
      }
    } catch (error: unknown) {
      setAuthError((error instanceof Error ? error.message : String(error)) || t('modals.auth.errors.authenticationFailed'));
    } finally {
      setAuthenticating(false);
    }
  };

  const handleStartGuestMode = async () => {
    const hasData = await checkDataAvailability();
    if (!hasData) {
      setAuthError(t('modals.auth.errors.guestModeNoData'));
      return;
    }

    await authService.startGuestMode();
    onAuthChanged?.();

    const setupResponse = await fetch('/api/system/setup', ApiService.getFetchOptions());
    const setupData = await setupResponse.json();

    if (setupData.isSetupCompleted) {
      handleInitializationComplete();
    } else {
      setCurrentStep('import-historical-data');
    }
  };

  const handleContinueAsAdmin = async () => {
    onAuthChanged?.();
    await checkPicsDataStatus();
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
      case 'import-historical-data':
        setCurrentStep('api-key');
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
            onAuthenticate={handleAuthenticate}
            onStartGuestMode={handleStartGuestMode}
            onContinueAsAdmin={handleContinueAsAdmin}
          />
        );

      case 'import-historical-data':
        return (
          <ImportHistoricalDataStep onComplete={handleImportComplete} onSkip={handleImportComplete} />
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
          <DepotMappingStep onComplete={handleDepotMappingComplete} onSkip={handleDepotMappingSkip} />
        );

      default:
        return null;
    }
  };

  const stepInfo = getStepInfo(t)[currentStep];

  return (
    <div className="fixed inset-0 z-[9999] flex items-center justify-center p-4 bg-themed-primary">
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
                title={backButtonDisabled ? t('initialization.modal.cannotGoBack') : t('initialization.modal.goBack')}
              >
                <ArrowLeft className="w-5 h-5" />
              </button>
            )}
            <div className="flex items-center gap-2">
              <Rocket className="w-5 h-5 text-primary" />
              <span className="font-semibold text-themed-primary">{t('initialization.modal.setupWizard')}</span>
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
        <div className="p-8 overflow-y-auto min-h-0">
          {renderStep()}
        </div>
      </div>
    </div>
  );
};

export default DepotInitializationModal;
