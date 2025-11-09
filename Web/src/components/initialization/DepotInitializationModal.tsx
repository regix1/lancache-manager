import React, { useState, useEffect } from 'react';
import { AlertTriangle } from 'lucide-react';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import { storage } from '@utils/storage';
import {
  ApiKeyStep,
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
  | 'steam-auth'
  | 'depot-init'
  | 'pics-progress'
  | 'log-processing'
  | 'depot-mapping';

const STEP_INFO: Record<InitStep, { number: number; title: string; total: number }> = {
  'api-key': { number: 1, title: 'Authentication', total: 6 },
  'steam-auth': { number: 2, title: 'Steam PICS Authentication', total: 6 },
  'depot-init': { number: 3, title: 'Depot Initialization', total: 6 },
  'pics-progress': { number: 4, title: 'PICS Data Progress', total: 6 },
  'log-processing': { number: 5, title: 'Log Processing', total: 6 },
  'depot-mapping': { number: 6, title: 'Depot Mapping', total: 6 }
};

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  onAuthChanged
}) => {
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    // Initialize from localStorage to survive page reloads
    const stored = storage.getItem('initializationCurrentStep');
    return (stored as InitStep) || 'api-key';
  });
  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);
  const [picsData, setPicsData] = useState<any>(null);
  const [initializing, setInitializing] = useState(() => {
    // Restore from localStorage to survive page reloads
    const stored = storage.getItem('initializationInProgress');
    return stored === 'true';
  });
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | 'continue' | null>(
    () => {
      // Restore from localStorage
      const stored = storage.getItem('initializationMethod');
      return (stored as 'cloud' | 'generate' | 'continue') || null;
    }
  );
  const [error, setError] = useState<string | null>(null);
  const [downloadStatus, setDownloadStatus] = useState<string | null>(() => {
    // Restore from localStorage
    return storage.getItem('initializationDownloadStatus') || null;
  });
  const [usingSteamAuth, setUsingSteamAuth] = useState<boolean>(() => {
    // Restore from localStorage
    const stored = storage.getItem('usingSteamAuth');
    return stored === 'true';
  });
  const [authDisabled, setAuthDisabled] = useState<boolean>(false);

  // Persist current step to localStorage whenever it changes
  useEffect(() => {
    storage.setItem('initializationCurrentStep', currentStep);
  }, [currentStep]);

  // Persist initialization state to localStorage
  useEffect(() => {
    storage.setItem('initializationInProgress', initializing.toString());
  }, [initializing]);

  useEffect(() => {
    if (selectedMethod) {
      storage.setItem('initializationMethod', selectedMethod);
    } else {
      storage.removeItem('initializationMethod');
    }
  }, [selectedMethod]);

  useEffect(() => {
    if (downloadStatus) {
      storage.setItem('initializationDownloadStatus', downloadStatus);
    } else {
      storage.removeItem('initializationDownloadStatus');
    }
  }, [downloadStatus]);

  // Persist usingSteamAuth to localStorage
  useEffect(() => {
    storage.setItem('usingSteamAuth', usingSteamAuth.toString());
  }, [usingSteamAuth]);

  // Wrapper to clear localStorage and call onInitialized
  const handleInitializationComplete = () => {
    storage.removeItem('initializationCurrentStep');
    storage.removeItem('initializationInProgress');
    storage.removeItem('initializationMethod');
    storage.removeItem('initializationDownloadStatus');
    storage.removeItem('usingSteamAuth');
    onInitialized();
  };

  useEffect(() => {
    const checkSetupStatus = async () => {
      await checkDataAvailability();

      // Check if authentication is globally disabled
      const authCheck = await authService.checkAuth();
      const authRequired = authCheck.requiresAuth;

      console.log('[DepotInit] Auth check:', {
        requiresAuth: authRequired,
        isAuthenticated: authCheck.isAuthenticated
      });

      // If we have a stored step from a previous session, validate it's still relevant
      const storedStep = storage.getItem('initializationCurrentStep');
      if (storedStep) {
        console.log('[DepotInit] Found stored step from localStorage:', storedStep);

        // Store auth disabled state
        setAuthDisabled(!authRequired);

        // Check if initialization state is stale (browser was closed mid-setup)
        // Only reset if auth is required AND there's NO auth at all
        // This prevents resetting on page refresh when API is slow to respond
        const isAuthenticated = storage.getItem('lancache_auth_registered') === 'true';
        const isGuestMode = storage.getItem('lancache_guest_expires') !== null;
        const hasAuth = isAuthenticated || isGuestMode;

        if (authRequired && !hasAuth && storedStep !== 'api-key') {
          console.log(
            '[DepotInit] Stale initialization state detected (no auth), resetting to step 1'
          );
          storage.removeItem('initializationCurrentStep');
          storage.removeItem('initializationInProgress');
          storage.removeItem('initializationMethod');
          storage.removeItem('initializationDownloadStatus');
          storage.removeItem('initializationFlowActive');
          storage.removeItem('usingSteamAuth');
          setCurrentStep('api-key');
          return;
        }

        console.log('[DepotInit] Restoring to step:', storedStep);

        // Explicitly set the current step to the stored step (in case it wasn't set during initialization)
        setCurrentStep(storedStep as InitStep);

        // Restore other state variables from localStorage
        const storedMethod = storage.getItem('initializationMethod');
        const storedInProgress = storage.getItem('initializationInProgress');
        const storedDownloadStatus = storage.getItem('initializationDownloadStatus');
        const storedUsingSteamAuth = storage.getItem('usingSteamAuth');

        // Restore state
        if (storedMethod) {
          setSelectedMethod(storedMethod as 'cloud' | 'generate' | 'continue');
        }
        if (storedInProgress === 'true') {
          setInitializing(true);
        }
        if (storedDownloadStatus) {
          setDownloadStatus(storedDownloadStatus);
        }
        if (storedUsingSteamAuth === 'true') {
          setUsingSteamAuth(true);
        }

        // Check if we were in the middle of a download when page reloaded
        if (
          (storedStep === 'depot-init' || storedStep === 'steam-auth') &&
          storedMethod === 'cloud' &&
          storedInProgress === 'true'
        ) {
          console.log('[DepotInit] Download was in progress, checking completion status...');
          // Check if download actually completed while page was reloading
          const picsStatus = await checkPicsDataStatus();
          if (picsStatus?.database?.totalMappings > 0) {
            console.log('[DepotInit] Download completed, moving to log-processing');
            setInitializing(false);
            setSelectedMethod(null);
            setDownloadStatus(null);
            storage.removeItem('initializationInProgress');
            storage.removeItem('initializationMethod');
            storage.removeItem('initializationDownloadStatus');
            setCurrentStep('log-processing');
            return;
          } else {
            console.log('[DepotInit] Download was interrupted by page reload');
            // Keep the UI state to show download was interrupted
            // User will see the download status and can retry
            setError('Download was interrupted by page reload. Please try again.');
            setInitializing(false);
            setSelectedMethod(null);
            setDownloadStatus(null);
            storage.removeItem('initializationInProgress');
            storage.removeItem('initializationMethod');
            storage.removeItem('initializationDownloadStatus');
          }
        }

        // Still need to check PICS data if we're past the steam-auth step
        if (
          storedStep === 'depot-init' ||
          storedStep === 'pics-progress' ||
          storedStep === 'log-processing' ||
          storedStep === 'depot-mapping'
        ) {
          checkPicsDataStatus();
        }
        return;
      }

      try {
        const setupResponse = await fetch('/api/management/setup-status');
        const setupData = await setupResponse.json();

        // Store auth disabled state
        setAuthDisabled(!authRequired);

        if (!setupData.isCompleted) {
          setCurrentStep('api-key');
          return;
        }

        // If auth is disabled, start at api-key step but with simplified UI
        if (!authRequired) {
          console.log(
            '[DepotInit] Authentication is globally disabled, showing simplified auth step'
          );
          setCurrentStep('api-key');
          return;
        }

        if (!authCheck.isAuthenticated) {
          setCurrentStep('api-key');
        } else {
          // Authenticated and setup complete - go to steam auth
          setCurrentStep('steam-auth');
          checkPicsDataStatus();
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setCurrentStep('api-key');
      }
    };

    checkSetupStatus();
  }, []);

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      // Check if log processing has been run by checking the setup status
      // This should have a flag indicating logs have been processed at least once
      const setupResponse = await fetch('/api/management/setup-status');

      if (setupResponse.ok) {
        const setupData = await setupResponse.json();
        // Enable guest mode if setup has been completed or if logs have been processed
        const hasData = setupData.isSetupCompleted || setupData.hasProcessedLogs || false;
        console.log('[DepotInit] Data availability check:', {
          isSetupCompleted: setupData.isSetupCompleted,
          hasProcessedLogs: setupData.hasProcessedLogs,
          hasData
        });
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
      await fetch('/api/management/mark-setup-completed', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
    }
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

          // Move to steam authentication step
          await checkPicsDataStatus();
          setCurrentStep('steam-auth');
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

    const setupResponse = await fetch('/api/management/setup-status');
    const setupData = await setupResponse.json();

    if (setupData.isSetupCompleted) {
      handleInitializationComplete();
    } else {
      setCurrentStep('steam-auth');
    }
  };

  const handleDownloadPrecreated = async () => {
    console.log('[DepotInit] handleDownloadPrecreated started');
    setInitializing(true);
    setSelectedMethod('cloud');
    setError(null);
    setDownloadStatus(null);

    try {
      // Step 1: Download from GitHub
      setDownloadStatus('Connecting to GitHub...');
      console.log('[DepotInit] Downloading precreated data from GitHub');

      // Add a small delay to show the first status
      await new Promise((resolve) => setTimeout(resolve, 500));

      setDownloadStatus('Downloading depot mappings from GitHub (290,000+ mappings)...');
      await ApiService.downloadPrecreatedDepotData();

      // Step 2: Import into database
      setDownloadStatus('Import complete! Finalizing setup...');
      console.log('[DepotInit] Download complete');

      // Step 3: Move to next step
      setDownloadStatus('Success! Moving to next step...');
      await new Promise((resolve) => setTimeout(resolve, 500));

      console.log('[DepotInit] Changing step to log-processing');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage since download is complete
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
      setCurrentStep('log-processing');
      console.log('[DepotInit] Step changed to log-processing');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleDownloadPrecreated:', err);
      setError(err.message || 'Failed to download pre-created depot data');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage on error
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
    }
  };

  const handleGenerateOwn = async () => {
    console.log('[DepotInit] handleGenerateOwn started');
    setInitializing(true);
    setSelectedMethod('generate');
    setError(null);

    try {
      console.log('[DepotInit] Triggering full rebuild');
      const response = await ApiService.triggerSteamKitRebuild(false);
      console.log('[DepotInit] Backend response:', response);

      // If backend requires full scan but we already requested full, something went wrong
      if (response.requiresFullScan) {
        console.error(
          '[DepotInit] Backend still requires full scan even though we requested full scan'
        );
        setError('Unable to start full scan. Please try again or download from GitHub.');
        setInitializing(false);
        setSelectedMethod(null);
        storage.removeItem('initializationInProgress');
        storage.removeItem('initializationMethod');
        storage.removeItem('initializationDownloadStatus');
        return;
      }

      console.log('[DepotInit] Changing step to pics-progress');
      setInitializing(false);
      setSelectedMethod(null);
      // Clear localStorage since generation started successfully
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
      setCurrentStep('pics-progress');
      console.log('[DepotInit] Step changed to pics-progress');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleGenerateOwn:', err);
      setError(err.message || 'Failed to start depot generation');
      setInitializing(false);
      setSelectedMethod(null);
      // Clear localStorage on error
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
    }
  };

  const handleContinue = async () => {
    console.log('[DepotInit] handleContinue started');
    setInitializing(true);
    setSelectedMethod('continue');
    setError(null);
    setDownloadStatus(null);

    try {
      // Check if JSON file exists and needs to be imported
      const picsStatus = await checkPicsDataStatus();
      console.log('[DepotInit] PICS status for Continue:', picsStatus);

      const hasJsonFile = picsStatus?.jsonFile?.exists === true;
      const hasDatabaseMappings = (picsStatus?.database?.totalMappings || 0) > 1000;

      if (hasJsonFile && !hasDatabaseMappings) {
        setDownloadStatus('Importing existing depot mappings from file...');
        console.log('[DepotInit] Importing JSON file to database before rebuild');

        // Import JSON to database first
        await fetch('/api/gameinfo/import-pics-data', {
          method: 'POST',
          headers: ApiService.getHeaders()
        });

        setDownloadStatus('Import complete! Starting incremental update...');
        await new Promise((resolve) => setTimeout(resolve, 500));
      } else {
        setDownloadStatus('Starting incremental update...');
      }

      console.log('[DepotInit] Triggering incremental rebuild');
      const response = await ApiService.triggerSteamKitRebuild(true);
      console.log('[DepotInit] Backend response:', response);

      // Check if backend says full scan is required
      if (response.requiresFullScan) {
        console.log(
          '[DepotInit] Backend requires full scan - automatically retrying with full scan'
        );
        setDownloadStatus(
          `Change gap too large (${response.changeGap || 'unknown'}). Starting full scan instead...`
        );
        await new Promise((resolve) => setTimeout(resolve, 1000));

        // Retry with full scan
        const fullScanResponse = await ApiService.triggerSteamKitRebuild(false);
        console.log('[DepotInit] Full scan response:', fullScanResponse);

        // If full scan also fails, show error
        if (fullScanResponse.requiresFullScan) {
          console.error('[DepotInit] Full scan also returned requiresFullScan');
          setError('Unable to start scan. Please try downloading from GitHub instead.');
          setInitializing(false);
          setSelectedMethod(null);
          setDownloadStatus(null);
          storage.removeItem('initializationInProgress');
          storage.removeItem('initializationMethod');
          storage.removeItem('initializationDownloadStatus');
          return;
        }
      }

      setDownloadStatus('Success! Moving to next step...');
      await new Promise((resolve) => setTimeout(resolve, 500));

      console.log('[DepotInit] Changing step to pics-progress');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage since continue started successfully
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
      setCurrentStep('pics-progress');
      console.log('[DepotInit] Step changed to pics-progress');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleContinue:', err);
      setError(err.message || 'Failed to run incremental update');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage on error
      storage.removeItem('initializationInProgress');
      storage.removeItem('initializationMethod');
      storage.removeItem('initializationDownloadStatus');
    }
  };

  const handleContinueAsAdmin = async () => {
    // When auth is disabled, just move to next step without authentication
    onAuthChanged?.();
    await checkPicsDataStatus();
    setCurrentStep('steam-auth');
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

      case 'steam-auth':
        return (
          <SteamPicsAuthStep
            onComplete={async (usingSteam: boolean) => {
              setUsingSteamAuth(usingSteam);
              await checkPicsDataStatus();
              setCurrentStep('depot-init');
            }}
          />
        );

      case 'depot-init':
        return (
          <DepotInitStep
            picsData={picsData}
            initializing={initializing}
            selectedMethod={selectedMethod}
            downloadStatus={downloadStatus}
            usingSteamAuth={usingSteamAuth}
            onDownloadPrecreated={handleDownloadPrecreated}
            onGenerateOwn={handleGenerateOwn}
            onContinue={handleContinue}
            onBackToSteamAuth={() => {
              setUsingSteamAuth(false);
              setCurrentStep('steam-auth');
            }}
          />
        );

      case 'pics-progress':
        return <PicsProgressStep onComplete={() => setCurrentStep('log-processing')} />;

      case 'log-processing':
        return (
          <LogProcessingStep
            onComplete={() => setCurrentStep('depot-mapping')}
            onSkip={async () => {
              await markSetupCompleted();
              handleInitializationComplete();
            }}
          />
        );

      case 'depot-mapping':
        return (
          <DepotMappingStep
            onComplete={async () => {
              await markSetupCompleted();
              handleInitializationComplete();
            }}
            onSkip={async () => {
              await markSetupCompleted();
              handleInitializationComplete();
            }}
          />
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
        {/* Step Indicator - Top Left */}
        <div
          className="absolute top-4 left-4 px-3 py-1.5 rounded-full text-xs font-semibold"
          style={{
            backgroundColor: 'var(--theme-primary)/10',
            color: 'var(--theme-primary)',
            border: '1px solid var(--theme-primary)/30'
          }}
        >
          Step {STEP_INFO[currentStep].number} of {STEP_INFO[currentStep].total}
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

        {/* Error Display */}
        {error && (
          <div
            className="p-4 rounded-lg mb-4"
            style={{
              backgroundColor: 'var(--theme-error-bg)',
              borderColor: 'var(--theme-error)',
              color: 'var(--theme-error-text)'
            }}
          >
            <p className="text-sm">{error}</p>
          </div>
        )}
      </div>
    </div>
  );
};

export default DepotInitializationModal;
