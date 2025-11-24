import React, { useState, useEffect } from 'react';
import { AlertTriangle, ArrowLeft } from 'lucide-react';
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
} from './steps';

interface DepotInitializationModalProps {
  onInitialized: () => void;
  onAuthChanged?: () => void;
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

const STEP_INFO: Record<InitStep, { number: number; title: string; total: number }> = {
  'api-key': { number: 1, title: 'Authentication', total: 9 },
  'import-historical-data': { number: 2, title: 'Import Historical Data', total: 9 },
  'data-source-choice': { number: 3, title: 'Data Source Selection', total: 9 },
  'steam-api-key': { number: 4, title: 'Steam API Key', total: 9 },
  'steam-auth': { number: 5, title: 'Steam PICS Authentication', total: 9 },
  'depot-init': { number: 6, title: 'Depot Initialization', total: 9 },
  'pics-progress': { number: 7, title: 'PICS Data Progress', total: 9 },
  'log-processing': { number: 8, title: 'Log Processing', total: 9 },
  'depot-mapping': { number: 9, title: 'Depot Mapping', total: 9 }
};

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  onAuthChanged
}) => {
  // Restore step from localStorage
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    const stored = storage.getItem('initializationCurrentStep');
    return (stored as InitStep) || 'api-key';
  });

  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);
  const [picsData, setPicsData] = useState<any>(null);
  const [usingSteamAuth, setUsingSteamAuth] = useState<boolean>(false);
  const [dataSourceChoice, setDataSourceChoice] = useState<'github' | 'steam' | null>(() => {
    const stored = storage.getItem('dataSourceChoice');
    return (stored as 'github' | 'steam') || null;
  });
  const [authDisabled, setAuthDisabled] = useState<boolean>(false);
  const [backButtonDisabled, setBackButtonDisabled] = useState<boolean>(false);

  // Persist current step whenever it changes
  useEffect(() => {
    storage.setItem('initializationCurrentStep', currentStep);
    console.log('[DepotInit] Step changed to:', currentStep);
  }, [currentStep]);

  // Persist data source choice
  useEffect(() => {
    if (dataSourceChoice) {
      storage.setItem('dataSourceChoice', dataSourceChoice);
    } else {
      storage.removeItem('dataSourceChoice');
    }
  }, [dataSourceChoice]);

  // Check setup status on mount
  useEffect(() => {
    const checkSetupStatus = async () => {
      // IMPORTANT: Check for stale localStorage data and clear if needed
      // Increment this version when localStorage structure changes to force cleanup
      const INIT_VERSION = '1.0';
      const storedVersion = storage.getItem('initializationVersion');

      if (storedVersion !== INIT_VERSION) {
        console.log(
          '[DepotInit] Stale or missing version detected (stored:',
          storedVersion,
          'expected:',
          INIT_VERSION,
          '), clearing all initialization localStorage'
        );
        clearAllLocalStorage();
        storage.setItem('initializationVersion', INIT_VERSION);
      }

      await checkDataAvailability();

      try {
        // Check if authentication is globally disabled
        const authCheck = await authService.checkAuth();
        const authRequired = authCheck.requiresAuth;
        setAuthDisabled(!authRequired);

        console.log('[DepotInit] Auth check:', {
          requiresAuth: authRequired,
          isAuthenticated: authCheck.isAuthenticated
        });

        // Check backend setup status
        const setupResponse = await fetch('/api/system/setup');
        const setupData = await setupResponse.json();

        // If setup is already complete, clear localStorage and close modal
        if (setupData.isCompleted && authCheck.isAuthenticated) {
          console.log('[DepotInit] Setup already complete, clearing localStorage and closing');
          clearAllLocalStorage();
          onInitialized();
          return;
        }

        // Check if we have a stored step - keep it if valid
        const storedStep = storage.getItem('initializationCurrentStep');
        if (storedStep) {
          console.log('[DepotInit] Restoring saved step:', storedStep);

          // Restore data source choice if we're on steps that need it
          const storedChoice = storage.getItem('dataSourceChoice');
          if (storedChoice) {
            console.log('[DepotInit] Restoring data source choice:', storedChoice);
            setDataSourceChoice(storedChoice as 'github' | 'steam');
          }

          // Load PICS data if needed for later steps
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

        // No stored step - determine initial step based on auth status
        if (!authRequired || !authCheck.isAuthenticated) {
          setCurrentStep('api-key');
        } else {
          // Already authenticated, skip to import step
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
    console.log('[DepotInit] Clearing all initialization localStorage');
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
      const setupResponse = await fetch('/api/system/setup');
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
      await fetch('/api/system/setup', {
        method: 'PATCH',
        headers: ApiService.getHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ completed: true })
      });
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
    }
  };

  const handleInitializationComplete = () => {
    clearAllLocalStorage();
    console.log('[DepotInit] Initialization complete, cleared localStorage');
    onInitialized();
  };

  // Step 1: API Key
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
          setAuthError('Authentication succeeded but verification failed');
        }
      } else {
        setAuthError(result.message);
      }
    } catch (error: any) {
      setAuthError(error.message || 'Authentication failed');
    } finally {
      setAuthenticating(false);
    }
  };

  const handleStartGuestMode = async () => {
    const hasData = await checkDataAvailability();
    if (!hasData) {
      setAuthError('Guest mode is not available. No data has been loaded yet.');
      return;
    }

    await authService.startGuestMode();
    onAuthChanged?.();

    const setupResponse = await fetch('/api/system/setup');
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

  // Step 2: Import Historical Data
  const handleImportComplete = () => {
    setCurrentStep('data-source-choice');
  };

  // Step 3: Data Source Choice
  const handleChooseGithub = () => {
    setDataSourceChoice('github');
    setCurrentStep('depot-init');
  };

  const handleChooseSteam = () => {
    setDataSourceChoice('steam');
    setCurrentStep('steam-api-key');
  };

  // Step 4: Steam API Key
  const handleSteamApiKeyComplete = async () => {
    await checkPicsDataStatus();
    setCurrentStep('steam-auth');
  };

  // Step 5: Steam Auth
  const handleSteamAuthComplete = async (usingSteam: boolean) => {
    setUsingSteamAuth(usingSteam);
    await checkPicsDataStatus();
    setCurrentStep('depot-init');
  };

  // Step 6: Depot Init
  const handleDepotInitComplete = () => {
    // GitHub download completed - skip to log processing
    setCurrentStep('log-processing');
  };

  const handleDepotInitGenerateOwn = () => {
    // User clicked "Generate Fresh" - advance to PICS progress
    setCurrentStep('pics-progress');
  };

  const handleDepotInitContinue = () => {
    // User clicked "Continue" (incremental update) - advance to PICS progress
    setCurrentStep('pics-progress');
  };

  // Step 7: PICS Progress
  const handlePicsProgressComplete = () => {
    setCurrentStep('log-processing');
  };

  // Step 8: Log Processing
  const handleLogProcessingComplete = () => {
    setCurrentStep('depot-mapping');
  };

  const handleLogProcessingSkip = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  // Step 9: Depot Mapping
  const handleDepotMappingComplete = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  const handleDepotMappingSkip = async () => {
    await markSetupCompleted();
    handleInitializationComplete();
  };

  // Back button navigation
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

  const canGoBack = currentStep !== 'api-key' && !backButtonDisabled;

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

  return (
    <div className="fixed inset-0 z-[9999] bg-[var(--theme-bg-primary)] flex items-center justify-center">
      {/* Background pattern */}
      <div
        className="absolute inset-0 opacity-5"
        style={{
          backgroundImage: `repeating-linear-gradient(45deg, transparent, transparent 35px, var(--theme-text-primary) 35px, var(--theme-text-primary) 70px)`
        }}
      />

      <div
        className="relative z-10 max-w-4xl w-full mx-4 p-8 rounded-2xl border-2 shadow-2xl"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: 'var(--theme-primary)'
        }}
      >
        {/* Step Indicator & Back Button - Top Left */}
        <div className="absolute top-4 left-4 flex items-center gap-3">
          {/* Back Button */}
          {currentStep !== 'api-key' && (
            <button
              onClick={backButtonDisabled ? undefined : handleGoBack}
              disabled={backButtonDisabled}
              className="group flex items-center gap-2 px-3 py-1.5 rounded-full text-xs font-semibold transition-all duration-200"
              style={{
                backgroundColor: backButtonDisabled
                  ? 'var(--theme-bg-tertiary)'
                  : 'var(--theme-bg-tertiary)',
                color: backButtonDisabled ? 'var(--theme-text-muted)' : 'var(--theme-text-secondary)',
                border: `1px solid ${backButtonDisabled ? 'var(--theme-border-tertiary)' : 'var(--theme-border-secondary)'}`,
                cursor: backButtonDisabled ? 'not-allowed' : 'pointer',
                opacity: backButtonDisabled ? 0.5 : 1
              }}
              onMouseEnter={(e) => {
                if (!backButtonDisabled) {
                  e.currentTarget.style.backgroundColor = 'var(--theme-primary)/10';
                  e.currentTarget.style.borderColor = 'var(--theme-primary)/30';
                  e.currentTarget.style.color = 'var(--theme-primary)';
                }
              }}
              onMouseLeave={(e) => {
                if (!backButtonDisabled) {
                  e.currentTarget.style.backgroundColor = 'var(--theme-bg-tertiary)';
                  e.currentTarget.style.borderColor = 'var(--theme-border-secondary)';
                  e.currentTarget.style.color = 'var(--theme-text-secondary)';
                }
              }}
              title={
                backButtonDisabled
                  ? 'Cannot go back while operation is in progress'
                  : 'Go back to previous step'
              }
            >
              <ArrowLeft
                size={14}
                className={`transition-transform duration-200 ${!backButtonDisabled && 'group-hover:-translate-x-0.5'}`}
              />
              <span className="hidden sm:inline">Back</span>
            </button>
          )}

          {/* Step Indicator */}
          <div
            className="px-3 py-1.5 rounded-full text-xs font-semibold"
            style={{
              backgroundColor: 'var(--theme-primary)/10',
              color: 'var(--theme-primary)',
              border: '1px solid var(--theme-primary)/30'
            }}
          >
            Step {STEP_INFO[currentStep].number} of {STEP_INFO[currentStep].total}
          </div>
        </div>

        {/* Header */}
        <div className="text-center mb-8">
          <div
            className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
            style={{ backgroundColor: 'var(--theme-primary)/10' }}
          >
            <AlertTriangle size={32} style={{ color: 'var(--theme-primary)' }} />
          </div>
          <h1 className="text-3xl font-bold text-themed-primary mb-2">
            Welcome to Lancache Manager
          </h1>
          <p className="text-lg text-themed-secondary">
            {currentStep === 'api-key'
              ? authDisabled
                ? 'Choose access mode'
                : 'Authentication required'
              : 'Initial setup'}
          </p>
        </div>

        {/* Content - Render current step */}
        <div className="mb-8">{renderStep()}</div>
      </div>
    </div>
  );
};

export default DepotInitializationModal;
