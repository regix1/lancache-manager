import React, { useState, useEffect } from 'react';
import { Cloud, Database, AlertTriangle, Loader, Key, Eye } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import authService from '@services/auth.service';

interface DepotInitializationModalProps {
  onInitialized: () => void;
  isAuthenticated: boolean;
  onAuthChanged?: () => void;
  apiKeyOnlyMode?: boolean; // When true, only show API key form, skip depot initialization
}

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  isAuthenticated,
  onAuthChanged,
  apiKeyOnlyMode = false
}) => {
  const [initializing, setInitializing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | 'auto-update' | 'continue' | null>(null);
  const [showApiKeyForm, setShowApiKeyForm] = useState(true); // Default to true, will be updated by useEffect
  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [picsData, setPicsData] = useState<any>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);

  useEffect(() => {
    const checkSetupStatus = async () => {
      // Check data availability first
      await checkDataAvailability();
      try {
        // If in API key only mode, always show the API key form and skip depot initialization
        if (apiKeyOnlyMode) {
          setShowApiKeyForm(true);
          return;
        }

        // Check if setup has been completed
        const setupResponse = await fetch('/api/management/setup-status');
        const setupData = await setupResponse.json();

        // If setup is not completed, always show API key form first
        if (!setupData.isSetupCompleted) {
          setShowApiKeyForm(true);
          return;
        }

        // If setup is completed, check authentication status
        const authCheck = await authService.checkAuth();
        const actuallyAuthenticated = authCheck.isAuthenticated;

        // Show API key form if not authenticated
        setShowApiKeyForm(!actuallyAuthenticated);

        // If the parent component thinks we're authenticated but we're actually not, let it know
        if (isAuthenticated && !actuallyAuthenticated) {
          onAuthChanged?.();
        }

        // Prefetch current PICS status immediately for authenticated users
        // This ensures the Continue button shows instantly without delay
        if (actuallyAuthenticated) {
          // Start fetching immediately without await
          checkPicsDataStatus().then(status => {
            setPicsData(status);
          }).catch(err => {
            console.warn('Failed to fetch PICS status:', err);
          });
        }
      } catch (error) {
        console.error('Failed to check setup/auth status:', error);
        // On error, show the API key form to be safe
        setShowApiKeyForm(true);
      }
    };

    // Only check setup status on initial mount, not when auth changes
    checkSetupStatus();
  }, [apiKeyOnlyMode]); // Added apiKeyOnlyMode as dependency

  // Check data availability
  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      const response = await fetch('/api/auth/check');
      if (response.ok) {
        const data = await response.json();
        // Data is available if setup has been completed OR data has been loaded
        // This allows guest mode when the system is live and has data, even on first login
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

  // Cleanup function to handle interrupted initialization
  useEffect(() => {
    return () => {
      // If component unmounts while initializing, reset state to prevent confusion
      if (initializing) {
        console.warn('Depot initialization was interrupted');
      }
    };
  }, [initializing]);

  const verifyDepotInitialization = async () => {
    let retries = 0;
    const maxRetries = 30; // Wait up to 30 seconds for verification

    while (retries < maxRetries) {
      try {
        const response = await fetch('/api/gameinfo/pics-status', {
          headers: ApiService.getHeaders()
        });

        if (response.ok) {
          const data = await response.json();
          // For verification, we need either:
          // 1. Rebuild is currently running (initialization in progress), OR
          // 2. Both JSON file exists AND we have data (complete initialization)
          const rebuildRunning = data.steamKit2?.isRebuildRunning === true;
          const hasJsonFile = data.jsonFile?.exists === true;
          const hasOtherData = (data.database?.totalMappings > 0) ||
                              (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0);

          const hasData = rebuildRunning || (hasJsonFile && hasOtherData);

          if (hasData) {
            setProgress('Initialization verified! Launching application...');
            setTimeout(() => {
              onInitialized();
            }, 1000);
            return;
          }
        }

        // Wait 1 second before retrying
        await new Promise(resolve => setTimeout(resolve, 1000));
        retries++;
        setProgress(`Verification attempt ${retries}/${maxRetries}...`);
      } catch (error) {
        console.warn('Verification attempt failed:', error);
        retries++;
        await new Promise(resolve => setTimeout(resolve, 1000));
      }
    }

    // If we get here, verification failed
    throw new Error('Failed to verify depot initialization. Please try again.');
  };

  const markSetupCompleted = async () => {
    try {
      const response = await fetch('/api/management/mark-setup-completed', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });

      if (!response.ok) {
        console.warn('Failed to mark setup as completed');
      }
    } catch (error) {
      console.warn('Failed to mark setup as completed:', error);
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

  const handleAutoUpdate = async () => {
    setInitializing(true);
    setSelectedMethod('auto-update');
    setError(null);
    setProgress('Checking existing PICS data for updates...');

    try {
      // Check if data exists and needs update
      const picsStatus = await checkPicsDataStatus();

      if (!picsStatus) {
        throw new Error('Unable to check PICS data status');
      }

      // Prioritize JSON file existence - if depot file is missing, force regeneration
      // even if database or SteamKit2 still have cached data
      const hasJsonFile = picsStatus.jsonFile?.exists === true;
      const hasOtherData =
        (picsStatus.database?.totalMappings > 0) ||
        (picsStatus.steamKit2?.isReady && picsStatus.steamKit2?.depotCount > 0);

      const hasExistingData = hasJsonFile && hasOtherData;

      if (hasExistingData) {
        setProgress('Existing PICS data found. Running incremental update...');

        // If JSON file exists but other data is missing, do full rebuild
        // Otherwise do incremental update
        const shouldDoFullRebuild = hasJsonFile && !hasOtherData;
        await ApiService.triggerSteamKitRebuild(!shouldDoFullRebuild);
        setProgress('Incremental update completed! Verifying initialization...');
        await verifyDepotInitialization();
        await markSetupCompleted();
      } else {
        // No existing data or depot file missing, treat as fresh setup
        if (!hasJsonFile) {
          throw new Error('Depot file missing. Please choose an initialization method to regenerate it.');
        } else {
          throw new Error('No existing PICS data found. Please choose an initialization method.');
        }
      }
    } catch (err: any) {
      setError(err.message || 'Failed to update PICS data');
      setInitializing(false);
      setSelectedMethod(null);
      setProgress(null);
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
        // Verify authentication status with server after successful registration
        const authCheck = await authService.checkAuth();
        if (authCheck.isAuthenticated) {
          // Notify parent component of authentication change
          onAuthChanged?.();

          // If in API key only mode, close the modal and don't proceed to depot initialization
          if (apiKeyOnlyMode) {
            setProgress('Authentication successful! Returning to application...');
            setTimeout(() => {
              onInitialized(); // This will close the modal
            }, 1000);
            return;
          }

          // Start loading depot options immediately
          setProgress('Authentication successful! Loading depot options...');

          // Fetch PICS data first before hiding form
          const picsStatus = await checkPicsDataStatus();
          setPicsData(picsStatus); // Update the state so UI can show current status

          // Now immediately hide the form - the data is already loaded
          setShowApiKeyForm(false);
          setProgress(null); // Clear progress immediately since data is loaded

          if (picsStatus) {
            // Check for JSON file first - if missing, don't auto-update
            const hasJsonFile = picsStatus.jsonFile?.exists === true;
            const hasOtherData =
              (picsStatus.database?.totalMappings > 0) ||
              (picsStatus.steamKit2?.isReady && picsStatus.steamKit2?.depotCount > 0);

            if (hasJsonFile && hasOtherData) {
              setProgress('Existing PICS data found! Running automatic update...');
              // Auto-run incremental update immediately
              handleAutoUpdate();
              return;
            } else if (!hasJsonFile && hasOtherData) {
              setProgress('Depot file missing but cached data found. Please choose initialization method to regenerate depot file.');
              setTimeout(() => setProgress(null), 5000);
            }
          }

          // No existing data found, show manual options
          // Don't show progress message, just clear it immediately
          setProgress(null);
        } else {
          setAuthError('Authentication succeeded but verification failed. Please try again.');
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

  const handleDownloadPrecreated = async () => {
    // Double-check authentication status before proceeding
    const authCheck = await authService.checkAuth();
    if (!authCheck.isAuthenticated) {
      setError('Authentication required. Please authenticate first.');
      setShowApiKeyForm(true);
      return;
    }

    setInitializing(true);
    setSelectedMethod('cloud');
    setError(null);
    setProgress('Checking for existing depot data...');

    try {
      // Check if PICS data already exists
      const picsStatus = await checkPicsDataStatus();

      if (picsStatus) {
        const hasExistingData =
          picsStatus.jsonFile?.exists === true ||
          (picsStatus.database?.totalMappings > 0) ||
          (picsStatus.steamKit2?.isReady && picsStatus.steamKit2?.depotCount > 0);

        if (hasExistingData) {
          // Check if data is complete (>100k mappings) for incremental, else full rebuild
          const hasCompleteData =
            (picsStatus.jsonFile?.totalMappings > 100000) ||
            (picsStatus.database?.totalMappings > 100000);

          if (hasCompleteData) {
            setProgress('Complete data found. Running incremental update to get latest changes...');
            await ApiService.triggerSteamKitRebuild(true);
          } else {
            setProgress('Incomplete data found. Running full generation to complete dataset...');
            await ApiService.triggerSteamKitRebuild(false);
          }
        } else {
          setProgress('Downloading pre-created depot data from GitHub...');
          await ApiService.downloadPrecreatedPicsData();
        }
      } else {
        setProgress('Downloading pre-created depot data from GitHub...');
        await ApiService.downloadPrecreatedPicsData();
      }

      setProgress('Import complete! Verifying initialization...');

      // Verify depot data actually exists before marking as initialized
      await verifyDepotInitialization();

      // Mark setup as completed
      await markSetupCompleted();
    } catch (err: any) {
      setError(err.message || 'Failed to download pre-created depot data');
      setInitializing(false);
      setSelectedMethod(null);
      setProgress(null);
    }
  };

  const handleGenerateOwn = async () => {
    // Double-check authentication status before proceeding
    const authCheck = await authService.checkAuth();
    if (!authCheck.isAuthenticated) {
      setError('Authentication required. Please authenticate first.');
      setShowApiKeyForm(true);
      return;
    }

    setInitializing(true);
    setSelectedMethod('generate');
    setError(null);
    setProgress('Starting fresh Steam PICS depot generation...');

    try {

      // Always do a full rebuild for "Generate Fresh" - never incremental
      await ApiService.triggerSteamKitRebuild(false);
      setProgress('Depot generation started! Verifying initialization...');

      // For generate method, we can proceed immediately since it runs in background
      // but still verify depot service is ready
      await verifyDepotInitialization();

      // Mark setup as completed
      await markSetupCompleted();
    } catch (err: any) {
      setError(err.message || 'Failed to start depot generation');
      setInitializing(false);
      setSelectedMethod(null);
      setProgress(null);
    }
  };

  // Helper function to determine if Continue option should be shown
  const shouldShowContinueOption = () => {
    if (!picsData) return false;

    // Show Continue option only if JSON mapping file already exists
    return picsData.jsonFile?.exists === true;
  };

  const handleContinue = async () => {
    // Double-check authentication status before proceeding
    const authCheck = await authService.checkAuth();
    if (!authCheck.isAuthenticated) {
      setError('Authentication required. Please authenticate first.');
      setShowApiKeyForm(true);
      return;
    }

    setInitializing(true);
    setSelectedMethod('continue');
    setError(null);
    setProgress('Running incremental update to check for new depot mappings...');

    try {
      // Run incremental update to get latest changes only
      await ApiService.triggerSteamKitRebuild(true);
      setProgress('Incremental update completed! Verifying initialization...');

      // Verify depot data is ready
      await verifyDepotInitialization();

      // Mark setup as completed
      await markSetupCompleted();
    } catch (err: any) {
      setError(err.message || 'Failed to run incremental update');
      setInitializing(false);
      setSelectedMethod(null);
      setProgress(null);
    }
  };

  const handleStartGuestMode = async () => {
    // Recheck data availability right before starting guest mode
    const hasData = await checkDataAvailability();

    if (!hasData) {
      setAuthError('Guest mode is not available. No data has been loaded yet. Please authenticate with an API key first.');
      return;
    }
    authService.startGuestMode();

    // Notify parent component of auth mode change
    onAuthChanged?.();

    // For API key only mode, we need to close the modal after setting guest mode
    if (apiKeyOnlyMode) {
      setProgress('Starting guest mode... (6 hour access)');
      setTimeout(() => {
        onInitialized(); // This will close the modal
      }, 1000);
    } else {
      // For initial setup, check if we need to show depot initialization
      const setupResponse = await fetch('/api/management/setup-status');
      const setupData = await setupResponse.json();

      if (setupData.isSetupCompleted) {
        // Setup is complete, just close and proceed
        onInitialized();
      } else {
        // Setup not complete, but in guest mode we can't mark it as completed (not authenticated)
        // Just proceed anyway - guest mode is read-only
        onInitialized();
      }
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
            {apiKeyOnlyMode ? 'Please enter your new API key' : 'Steam depot initialization required'}
          </p>
        </div>

        {/* Content */}
        <div className="mb-8">
          {showApiKeyForm ? (
            <>
              <p className="text-themed-secondary text-center mb-6">
                {apiKeyOnlyMode
                  ? 'Your API key has been regenerated. Enter the new API key for full access, or continue as guest to view data only:'
                  : 'Enter your API key for full management access, or continue as guest to view data for 6 hours:'
                }
              </p>

              {/* API Key Form */}
              <div className="space-y-4">
                <div>
                  <label className="block text-sm font-medium text-themed-primary mb-2">
                    API Key
                  </label>
                  <input
                    type="text"
                    value={apiKey}
                    onChange={(e) => setApiKey(e.target.value)}
                    placeholder="Enter your API key here..."
                    className="w-full p-3 text-sm themed-input"
                    disabled={authenticating}
                  />
                </div>


                <div className="flex flex-col gap-3">
                  <Button
                    variant="filled"
                    color="blue"
                    leftSection={authenticating ? <Loader className="w-4 h-4 animate-spin" /> : <Key className="w-4 h-4" />}
                    onClick={handleAuthenticate}
                    disabled={authenticating || !apiKey.trim()}
                    fullWidth
                  >
                    {authenticating ? 'Authenticating...' : 'Authenticate'}
                  </Button>

                  <div className="flex items-center gap-4">
                    <div className="flex-1 h-px bg-themed-border"></div>
                    <span className="text-xs text-themed-muted">OR</span>
                    <div className="flex-1 h-px bg-themed-border"></div>
                  </div>

                  <Button
                    variant="default"
                    leftSection={<Eye className="w-4 h-4" />}
                    onClick={handleStartGuestMode}
                    disabled={authenticating || checkingDataAvailability || !dataAvailable}
                    fullWidth
                    title={!dataAvailable ? 'No data available. Please authenticate first.' : 'View data for 6 hours'}
                  >
                    {!dataAvailable ? 'Guest Mode (No Data Available)' : 'Continue as Guest (6 hours)'}
                  </Button>
                </div>
              </div>

              {/* API Key Help */}
              <div className="mt-6 p-4 rounded-lg"
                   style={{
                     backgroundColor: 'var(--theme-info-bg)',
                     borderColor: 'var(--theme-info)',
                     color: 'var(--theme-info-text)'
                   }}>
                <p className="text-sm">
                  <strong>Where to find your API key:</strong><br/>
                  The API key was displayed when you first started the server. Check your server logs for "API Key:" or look in the <code>data/api_key.txt</code> file.
                </p>
              </div>
            </>
          ) : (
            <>
              <p className="text-themed-secondary text-center mb-6">
                To identify Steam games from your cache logs, depot mapping data is required.
                {picsData ? 'Choose how you\'d like to initialize this data:' : 'Choose how you\'d like to initialize this data:'}
              </p>

              {/* Show PICS data status if available */}
              {picsData && (
                <div className="mb-6 p-4 rounded-lg"
                     style={{
                       backgroundColor: 'var(--theme-info-bg)',
                       borderColor: 'var(--theme-info)',
                       color: 'var(--theme-info-text)'
                     }}>
                  <p className="text-sm">
                    <strong>Current PICS Data Status:</strong><br/>
                    {picsData.jsonFile?.exists && (
                      <>JSON File: {picsData.jsonFile?.totalMappings?.toLocaleString() ?? 0} mappings<br/></>
                    )}
                    Database: {picsData.database?.totalMappings?.toLocaleString() ?? 0} mappings<br/>
                    SteamKit2: {picsData.steamKit2?.depotCount?.toLocaleString() ?? 0} depots
                    {picsData.steamKit2?.isReady ? ' (Ready)' : ' (Not Ready)'}
                  </p>
                </div>
              )}

              {/* Options */}
              <div className={`grid grid-cols-1 gap-4 ${shouldShowContinueOption() ? 'md:grid-cols-3' : 'md:grid-cols-2'}`}>
            {/* Cloud Download Option */}
            <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
                 style={{
                   backgroundColor: selectedMethod === 'cloud' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                   borderColor: selectedMethod === 'cloud' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
                   minHeight: '280px'
                 }}>
              <div className="flex items-center gap-2 mb-3">
                <Cloud size={20} style={{ color: 'var(--theme-info)' }} />
                <h3 className="text-base font-semibold text-themed-primary">Pre-created Data</h3>
              </div>
              <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
                Download community-maintained depot mappings from GitHub.
                {picsData && (picsData.database?.totalMappings > 0 || picsData.steamKit2?.depotCount > 0) &&
                  <span className="text-themed-success"> Will update existing data.</span>
                }
              </p>
              <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
                <li>✓ Quick setup (~30 seconds)</li>
                <li>✓ 290,000+ mappings ready</li>
                <li>✓ Regularly updated</li>
                <li>✓ Won't delete existing data</li>
              </ul>
              <Button
                variant="filled"
                color="blue"
                size="sm"
                leftSection={initializing && selectedMethod === 'cloud' ? <Loader className="w-3 h-3 animate-spin" /> : <Cloud className="w-3 h-3" />}
                onClick={handleDownloadPrecreated}
                disabled={initializing || showApiKeyForm}
                fullWidth
                className="mt-auto"
              >
                {initializing && selectedMethod === 'cloud' ? 'Downloading...' : 'Download Pre-created'}
              </Button>
            </div>

            {/* Generate Own Option */}
            <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
                 style={{
                   backgroundColor: selectedMethod === 'generate' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                   borderColor: selectedMethod === 'generate' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
                   minHeight: '280px'
                 }}>
              <div className="flex items-center gap-2 mb-3">
                <Database size={20} style={{ color: 'var(--theme-success)' }} />
                <h3 className="text-base font-semibold text-themed-primary">Generate Fresh</h3>
              </div>
              <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
                Build your own depot mappings directly from Steam.
                <span className="text-themed-warning"> Always starts fresh - overwrites existing data.</span>
              </p>
              <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
                <li>✓ Latest data from Steam</li>
                <li>✓ Complete fresh rebuild</li>
                <li>✓ Overwrites any existing data</li>
                <li>○ Takes 10-30 minutes for full scan</li>
              </ul>
              <Button
                variant="filled"
                color="green"
                size="sm"
                leftSection={initializing && selectedMethod === 'generate' ? <Loader className="w-3 h-3 animate-spin" /> : <Database className="w-3 h-3" />}
                onClick={handleGenerateOwn}
                disabled={initializing || showApiKeyForm}
                fullWidth
                className="mt-auto"
              >
                {initializing && selectedMethod === 'generate' ? 'Processing...' : 'Generate Fresh Data'}
              </Button>
            </div>

            {/* Continue Option - Show only when JSON data exists */}
            {shouldShowContinueOption() && (
              <div className="p-5 rounded-lg border-2 transition-all flex flex-col"
                   style={{
                     backgroundColor: selectedMethod === 'continue' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                     borderColor: selectedMethod === 'continue' ? 'var(--theme-primary)' : 'var(--theme-border-primary)',
                     minHeight: '280px'
                   }}>
                <div className="flex items-center gap-2 mb-3">
                  <Database size={20} style={{ color: 'var(--theme-warning)' }} />
                  <h3 className="text-base font-semibold text-themed-primary">Continue</h3>
                </div>
                <p className="text-sm text-themed-secondary mb-3 min-h-[40px]">
                  Update existing depot mappings with latest changes from Steam.
                  <span className="text-themed-success"> Incremental update only.</span>
                </p>
                <ul className="text-xs text-themed-muted space-y-1 mb-4 flex-grow">
                  <li>✓ Fast incremental update (~1-2 minutes)</li>
                  <li>✓ Uses existing {picsData?.jsonFile?.totalMappings?.toLocaleString()} mappings</li>
                  <li>✓ Fetches only new/changed data</li>
                  <li>✓ Perfect for regular updates</li>
                </ul>
                <Button
                  variant="filled"
                  color="orange"
                  size="sm"
                  leftSection={initializing && selectedMethod === 'continue' ? <Loader className="w-3 h-3 animate-spin" /> : <Database className="w-3 h-3" />}
                  onClick={handleContinue}
                  disabled={initializing || showApiKeyForm}
                  fullWidth
                  className="mt-auto"
                >
                  {initializing && selectedMethod === 'continue' ? 'Updating...' : 'Continue with Update'}
                </Button>
              </div>
            )}
              </div>
            </>
          )}
        </div>

        {/* Progress/Error Display */}
        {progress && (
          <div className="p-4 rounded-lg mb-4"
               style={{
                 backgroundColor: 'var(--theme-info-bg)',
                 borderColor: 'var(--theme-info)',
                 color: 'var(--theme-info-text)'
               }}>
            <p className="text-sm flex items-center gap-2">
              <Loader className="w-4 h-4 animate-spin" />
              {progress}
            </p>
          </div>
        )}

        {authError && (
          <div className="p-4 rounded-lg mb-4"
               style={{
                 backgroundColor: 'var(--theme-error-bg)',
                 borderColor: 'var(--theme-error)',
                 color: 'var(--theme-error-text)'
               }}>
            <p className="text-sm">{authError}</p>
          </div>
        )}

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

        {/* Authentication Status */}
        {showApiKeyForm && (
          <div className="text-center text-xs text-themed-muted">
            <AlertTriangle className="inline w-4 h-4 mr-1" />
            Please authenticate to continue with depot initialization.
          </div>
        )}
      </div>
    </div>
  );
};

export default DepotInitializationModal;