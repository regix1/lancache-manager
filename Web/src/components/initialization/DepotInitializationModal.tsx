import React, { useState, useEffect } from 'react';
import { AlertTriangle } from 'lucide-react';
import authService from '@services/auth.service';
import ApiService from '@services/api.service';
import {
  ApiKeyStep,
  DepotInitStep,
  PicsProgressStep,
  LogProcessingStep,
  DepotMappingStep
} from './steps';

interface DepotInitializationModalProps {
  onInitialized: () => void;
  onAuthChanged?: () => void;
  apiKeyOnlyMode?: boolean;
}

type InitStep = 'api-key' | 'depot-init' | 'pics-progress' | 'log-processing' | 'depot-mapping';

const STEP_INFO: Record<InitStep, { number: number; title: string; total: number }> = {
  'api-key': { number: 1, title: 'Authentication', total: 5 },
  'depot-init': { number: 2, title: 'Depot Initialization', total: 5 },
  'pics-progress': { number: 3, title: 'PICS Data Progress', total: 5 },
  'log-processing': { number: 4, title: 'Log Processing', total: 5 },
  'depot-mapping': { number: 5, title: 'Depot Mapping', total: 5 }
};

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  onAuthChanged,
  apiKeyOnlyMode = false
}) => {
  const [currentStep, setCurrentStep] = useState<InitStep>(() => {
    // Initialize from localStorage to survive page reloads
    const stored = localStorage.getItem('initializationCurrentStep');
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
    const stored = localStorage.getItem('initializationInProgress');
    return stored === 'true';
  });
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | 'continue' | null>(() => {
    // Restore from localStorage
    const stored = localStorage.getItem('initializationMethod');
    return (stored as 'cloud' | 'generate' | 'continue') || null;
  });
  const [error, setError] = useState<string | null>(null);
  const [downloadStatus, setDownloadStatus] = useState<string | null>(() => {
    // Restore from localStorage
    return localStorage.getItem('initializationDownloadStatus') || null;
  });

  // Persist current step to localStorage whenever it changes
  useEffect(() => {
    localStorage.setItem('initializationCurrentStep', currentStep);
  }, [currentStep]);

  // Persist initialization state to localStorage
  useEffect(() => {
    localStorage.setItem('initializationInProgress', initializing.toString());
  }, [initializing]);

  useEffect(() => {
    if (selectedMethod) {
      localStorage.setItem('initializationMethod', selectedMethod);
    } else {
      localStorage.removeItem('initializationMethod');
    }
  }, [selectedMethod]);

  useEffect(() => {
    if (downloadStatus) {
      localStorage.setItem('initializationDownloadStatus', downloadStatus);
    } else {
      localStorage.removeItem('initializationDownloadStatus');
    }
  }, [downloadStatus]);

  // Wrapper to clear localStorage and call onInitialized
  const handleInitializationComplete = () => {
    localStorage.removeItem('initializationCurrentStep');
    localStorage.removeItem('initializationInProgress');
    localStorage.removeItem('initializationMethod');
    localStorage.removeItem('initializationDownloadStatus');
    onInitialized();
  };

  useEffect(() => {
    const checkSetupStatus = async () => {
      await checkDataAvailability();

      // If we have a stored step from a previous session, validate it's still relevant
      const storedStep = localStorage.getItem('initializationCurrentStep');
      if (storedStep) {
        console.log('[DepotInit] Found stored step from localStorage:', storedStep);

        // Check if initialization state is stale (browser was closed mid-setup)
        // If user is not authenticated and we're past step 1, it's stale - reset to step 1
        const authCheck = await authService.checkAuth();
        if (!authCheck.isAuthenticated && storedStep !== 'api-key') {
          console.log('[DepotInit] Stale initialization state detected (not authenticated), resetting to step 1');
          localStorage.removeItem('initializationCurrentStep');
          localStorage.removeItem('initializationInProgress');
          localStorage.removeItem('initializationMethod');
          localStorage.removeItem('initializationDownloadStatus');
          localStorage.removeItem('initializationFlowActive');
          setCurrentStep('api-key');
          return;
        }

        console.log('[DepotInit] Restoring to step:', storedStep);

        // Check if we were in the middle of a download when page reloaded
        const storedMethod = localStorage.getItem('initializationMethod');
        const storedInProgress = localStorage.getItem('initializationInProgress');

        if (storedStep === 'depot-init' && storedMethod === 'cloud' && storedInProgress === 'true') {
          console.log('[DepotInit] Download was in progress, checking completion status...');
          // Check if download actually completed while page was reloading
          const picsStatus = await checkPicsDataStatus();
          if (picsStatus?.database?.totalMappings > 0) {
            console.log('[DepotInit] Download completed, moving to log-processing');
            setInitializing(false);
            setSelectedMethod(null);
            setDownloadStatus(null);
            localStorage.removeItem('initializationInProgress');
            localStorage.removeItem('initializationMethod');
            localStorage.removeItem('initializationDownloadStatus');
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
            localStorage.removeItem('initializationInProgress');
            localStorage.removeItem('initializationMethod');
            localStorage.removeItem('initializationDownloadStatus');
          }
        }

        // Still need to check PICS data if we're past the depot-init step
        if (storedStep === 'pics-progress' || storedStep === 'log-processing' || storedStep === 'depot-mapping') {
          checkPicsDataStatus();
        }
        return;
      }

      try {
        if (apiKeyOnlyMode) {
          setCurrentStep('api-key');
          return;
        }

        const setupResponse = await fetch('/api/management/setup-status');
        const setupData = await setupResponse.json();

        if (!setupData.isSetupCompleted) {
          setCurrentStep('api-key');
          return;
        }

        const authCheck = await authService.checkAuth();
        if (!authCheck.isAuthenticated) {
          setCurrentStep('api-key');
        } else {
          // Authenticated and setup complete - go to depot init
          setCurrentStep('depot-init');
          checkPicsDataStatus();
        }
      } catch (error) {
        console.error('Failed to check setup status:', error);
        setCurrentStep('api-key');
      }
    };

    checkSetupStatus();
  }, [apiKeyOnlyMode]);

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      const response = await fetch('/api/auth/check');
      if (response.ok) {
        const data = await response.json();
        const hasData = data.hasBeenInitialized || data.hasDataLoaded || false;
        setDataAvailable(hasData);
        return hasData;
      }
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

          if (apiKeyOnlyMode) {
            setTimeout(() => handleInitializationComplete(), 1000);
            return;
          }

          // Move to depot initialization step
          await checkPicsDataStatus();
          setCurrentStep('depot-init');
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

    authService.startGuestMode();
    onAuthChanged?.();

    if (apiKeyOnlyMode) {
      setTimeout(() => handleInitializationComplete(), 1000);
    } else {
      const setupResponse = await fetch('/api/management/setup-status');
      const setupData = await setupResponse.json();

      if (setupData.isSetupCompleted) {
        handleInitializationComplete();
      } else {
        setCurrentStep('depot-init');
      }
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
      await new Promise(resolve => setTimeout(resolve, 500));

      setDownloadStatus('Downloading depot mappings from GitHub (290,000+ mappings)...');
      await ApiService.downloadPrecreatedPicsData();

      // Step 2: Import into database
      setDownloadStatus('Import complete! Finalizing setup...');
      console.log('[DepotInit] Download complete, marking setup as completed');
      await markSetupCompleted();

      // Step 3: Move to next step
      setDownloadStatus('Success! Moving to next step...');
      await new Promise(resolve => setTimeout(resolve, 500));

      console.log('[DepotInit] Changing step to log-processing');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage since download is complete
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
      setCurrentStep('log-processing');
      console.log('[DepotInit] Step changed to log-processing');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleDownloadPrecreated:', err);
      setError(err.message || 'Failed to download pre-created depot data');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage on error
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
    }
  };

  const handleGenerateOwn = async () => {
    console.log('[DepotInit] handleGenerateOwn started');
    setInitializing(true);
    setSelectedMethod('generate');
    setError(null);

    try {
      console.log('[DepotInit] Triggering full rebuild');
      await ApiService.triggerSteamKitRebuild(false);
      console.log('[DepotInit] Marking setup completed');
      await markSetupCompleted();
      console.log('[DepotInit] Changing step to pics-progress');
      setInitializing(false);
      setSelectedMethod(null);
      // Clear localStorage since generation started successfully
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
      setCurrentStep('pics-progress');
      console.log('[DepotInit] Step changed to pics-progress');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleGenerateOwn:', err);
      setError(err.message || 'Failed to start depot generation');
      setInitializing(false);
      setSelectedMethod(null);
      // Clear localStorage on error
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
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
        await new Promise(resolve => setTimeout(resolve, 500));
      } else {
        setDownloadStatus('Starting incremental update...');
      }

      console.log('[DepotInit] Triggering incremental rebuild');
      await ApiService.triggerSteamKitRebuild(true);

      console.log('[DepotInit] Marking setup completed');
      await markSetupCompleted();

      setDownloadStatus('Success! Moving to next step...');
      await new Promise(resolve => setTimeout(resolve, 500));

      console.log('[DepotInit] Changing step to pics-progress');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage since continue started successfully
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
      setCurrentStep('pics-progress');
      console.log('[DepotInit] Step changed to pics-progress');
    } catch (err: any) {
      console.error('[DepotInit] Error in handleContinue:', err);
      setError(err.message || 'Failed to run incremental update');
      setInitializing(false);
      setSelectedMethod(null);
      setDownloadStatus(null);
      // Clear localStorage on error
      localStorage.removeItem('initializationInProgress');
      localStorage.removeItem('initializationMethod');
      localStorage.removeItem('initializationDownloadStatus');
    }
  };

  const renderStep = () => {
    console.log('[DepotInit] Rendering step:', currentStep);
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
            apiKeyOnlyMode={apiKeyOnlyMode}
            onAuthenticate={handleAuthenticate}
            onStartGuestMode={handleStartGuestMode}
          />
        );

      case 'depot-init':
        return (
          <DepotInitStep
            picsData={picsData}
            initializing={initializing}
            selectedMethod={selectedMethod}
            downloadStatus={downloadStatus}
            onDownloadPrecreated={handleDownloadPrecreated}
            onGenerateOwn={handleGenerateOwn}
            onContinue={handleContinue}
          />
        );

      case 'pics-progress':
        return (
          <PicsProgressStep
            onComplete={() => setCurrentStep('log-processing')}
          />
        );

      case 'log-processing':
        return (
          <LogProcessingStep
            onComplete={() => setCurrentStep('depot-mapping')}
            onSkip={() => handleInitializationComplete()}
          />
        );

      case 'depot-mapping':
        return (
          <DepotMappingStep
            onComplete={() => handleInitializationComplete()}
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
          backgroundImage: `repeating-linear-gradient(45deg, transparent, transparent 35px, var(--theme-text-primary) 35px, var(--theme-text-primary) 70px)`,
        }}
      />

      <div className="relative z-10 max-w-4xl w-full mx-4 p-8 rounded-2xl border-2 shadow-2xl"
           style={{
             backgroundColor: 'var(--theme-bg-secondary)',
             borderColor: 'var(--theme-primary)'
           }}>

        {/* Step Indicator - Top Left */}
        {!apiKeyOnlyMode && (
          <div className="absolute top-4 left-4 px-3 py-1.5 rounded-full text-xs font-semibold"
               style={{
                 backgroundColor: 'var(--theme-primary)/10',
                 color: 'var(--theme-primary)',
                 border: '1px solid var(--theme-primary)/30'
               }}>
            Step {STEP_INFO[currentStep].number} of {STEP_INFO[currentStep].total}
          </div>
        )}

        {/* Header */}
        <div className="text-center mb-8">
          <div className="inline-flex items-center justify-center w-16 h-16 rounded-full mb-4"
               style={{ backgroundColor: 'var(--theme-primary)/10' }}>
            <AlertTriangle size={32} style={{ color: 'var(--theme-primary)' }} />
          </div>
          <h1 className="text-3xl font-bold text-themed-primary mb-2">
            {apiKeyOnlyMode ? 'API Key Regenerated' : 'Welcome to Lancache Manager'}
          </h1>
          <p className="text-lg text-themed-secondary">
            {apiKeyOnlyMode
              ? 'Please enter your new API key'
              : currentStep === 'api-key'
              ? 'Authentication required'
              : 'Initial setup'}
          </p>
        </div>

        {/* Content - Render current step */}
        <div className="mb-8">
          {renderStep()}
        </div>

        {/* Error Display */}
        {error && (
          <div className="p-4 rounded-lg mb-4"
               style={{
                 backgroundColor: 'var(--theme-error-bg)',
                 borderColor: 'var(--theme-error)',
                 color: 'var(--theme-error-text)'
               }}>
            <p className="text-sm">{error}</p>
          </div>
        )}
      </div>
    </div>
  );
};

export default DepotInitializationModal;
