import React, { useState, useEffect } from 'react';
import { Cloud, Database, AlertTriangle, Loader, Key } from 'lucide-react';
import { Button } from '@components/ui/Button';
import ApiService from '@services/api.service';
import authService from '@services/auth.service';

interface DepotInitializationModalProps {
  onInitialized: () => void;
  isAuthenticated: boolean;
  onAuthChanged?: () => void;
}

const DepotInitializationModal: React.FC<DepotInitializationModalProps> = ({
  onInitialized,
  isAuthenticated,
  onAuthChanged
}) => {
  const [initializing, setInitializing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [progress, setProgress] = useState<string | null>(null);
  const [selectedMethod, setSelectedMethod] = useState<'cloud' | 'generate' | null>(null);
  const [showApiKeyForm, setShowApiKeyForm] = useState(true); // Default to true, will be updated by useEffect
  const [apiKey, setApiKey] = useState('');
  const [deviceName, setDeviceName] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);

  useEffect(() => {
    const checkSetupStatus = async () => {
      try {
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
      } catch (error) {
        console.error('Failed to check setup/auth status:', error);
        // On error, show the API key form to be safe
        setShowApiKeyForm(true);
      }
    };

    // Only check setup status on initial mount, not when auth changes
    checkSetupStatus();
  }, []); // Removed dependencies to prevent re-checking on auth changes

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
          // Check if we have database mappings, SteamKit2 data, OR if rebuild is in progress
          const hasData = (data.database?.totalMappings > 0) ||
                         (data.steamKit2?.isReady && data.steamKit2?.depotCount > 0) ||
                         (data.steamKit2?.isRebuildRunning === true);

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

  const handleAuthenticate = async () => {
    if (!apiKey.trim()) {
      setAuthError('API key is required');
      return;
    }

    setAuthenticating(true);
    setAuthError(null);

    try {
      const result = await authService.register(apiKey, deviceName.trim() || null);
      if (result.success) {
        // Verify authentication status with server after successful registration
        const authCheck = await authService.checkAuth();
        if (authCheck.isAuthenticated) {
          setShowApiKeyForm(false);
          setProgress('Authentication successful! Choose your depot initialization method below.');
          setTimeout(() => setProgress(null), 3000);

          // Notify parent component of authentication change
          onAuthChanged?.();
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
    setProgress('Downloading pre-created depot data from GitHub...');

    try {
      const response = await fetch('/api/gameinfo/download-precreated-data', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const result = await response.json();
        setProgress('Import complete! Verifying initialization...');

        // Verify depot data actually exists before marking as initialized
        await verifyDepotInitialization();

        // Mark setup as completed
        await markSetupCompleted();
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Failed to download depot data');
      }
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
    setProgress('Starting Steam PICS depot generation...');

    try {
      const response = await fetch('/api/gameinfo/steamkit/rebuild', {
        method: 'POST',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const result = await response.json();
        setProgress('Depot generation started! Verifying initialization...');

        // For generate method, we can proceed immediately since it runs in background
        // but still verify depot service is ready
        await verifyDepotInitialization();

        // Mark setup as completed
        await markSetupCompleted();
      } else {
        const errorData = await response.json();
        throw new Error(errorData.error || 'Failed to start depot generation');
      }
    } catch (err: any) {
      setError(err.message || 'Failed to start depot generation');
      setInitializing(false);
      setSelectedMethod(null);
      setProgress(null);
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

      <div className="relative z-10 max-w-2xl w-full mx-4 p-8 rounded-2xl border-2 shadow-2xl"
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
            Welcome to Lancache Manager
          </h1>
          <p className="text-lg text-themed-secondary">
            Steam depot initialization required
          </p>
        </div>

        {/* Content */}
        <div className="mb-8">
          {showApiKeyForm ? (
            <>
              <p className="text-themed-secondary text-center mb-6">
                Please enter your API key to authenticate and continue with setup:
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
                    className="w-full p-3 rounded-lg border text-sm"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-primary)',
                      color: 'var(--theme-text-primary)'
                    }}
                    disabled={authenticating}
                  />
                </div>

                <div>
                  <label className="block text-sm font-medium text-themed-primary mb-2">
                    Device Name (Optional)
                  </label>
                  <input
                    type="text"
                    value={deviceName}
                    onChange={(e) => setDeviceName(e.target.value)}
                    placeholder="e.g., My Desktop"
                    className="w-full p-3 rounded-lg border text-sm"
                    style={{
                      backgroundColor: 'var(--theme-bg-tertiary)',
                      borderColor: 'var(--theme-border-primary)',
                      color: 'var(--theme-text-primary)'
                    }}
                    disabled={authenticating}
                  />
                </div>

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
                Choose how you'd like to initialize this data:
              </p>

              {/* Options */}
              <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
            {/* Cloud Download Option */}
            <div className="p-6 rounded-lg border-2 transition-all"
                 style={{
                   backgroundColor: selectedMethod === 'cloud' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                   borderColor: selectedMethod === 'cloud' ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
                 }}>
              <div className="flex items-center gap-3 mb-3">
                <Cloud size={24} style={{ color: 'var(--theme-info)' }} />
                <h3 className="text-lg font-semibold text-themed-primary">Pre-created Data</h3>
              </div>
              <p className="text-sm text-themed-secondary mb-3">
                Download community-maintained depot mappings from GitHub.
              </p>
              <ul className="text-xs text-themed-muted space-y-1 mb-4">
                <li>✓ Quick setup (~30 seconds)</li>
                <li>✓ 290,000+ mappings ready</li>
                <li>✓ Regularly updated</li>
              </ul>
              <Button
                variant="filled"
                color="blue"
                leftSection={initializing && selectedMethod === 'cloud' ? <Loader className="w-4 h-4 animate-spin" /> : <Cloud className="w-4 h-4" />}
                onClick={handleDownloadPrecreated}
                disabled={initializing || showApiKeyForm}
                fullWidth
              >
                {initializing && selectedMethod === 'cloud' ? 'Downloading...' : 'Download Pre-created'}
              </Button>
            </div>

            {/* Generate Own Option */}
            <div className="p-6 rounded-lg border-2 transition-all"
                 style={{
                   backgroundColor: selectedMethod === 'generate' ? 'var(--theme-primary)/10' : 'var(--theme-bg-tertiary)',
                   borderColor: selectedMethod === 'generate' ? 'var(--theme-primary)' : 'var(--theme-border-primary)'
                 }}>
              <div className="flex items-center gap-3 mb-3">
                <Database size={24} style={{ color: 'var(--theme-success)' }} />
                <h3 className="text-lg font-semibold text-themed-primary">Generate Fresh</h3>
              </div>
              <p className="text-sm text-themed-secondary mb-3">
                Build your own depot mappings directly from Steam.
              </p>
              <ul className="text-xs text-themed-muted space-y-1 mb-4">
                <li>✓ Latest data from Steam</li>
                <li>✓ Customized to your needs</li>
                <li>○ Takes 10-30 minutes</li>
              </ul>
              <Button
                variant="filled"
                color="green"
                leftSection={initializing && selectedMethod === 'generate' ? <Loader className="w-4 h-4 animate-spin" /> : <Database className="w-4 h-4" />}
                onClick={handleGenerateOwn}
                disabled={initializing || showApiKeyForm}
                fullWidth
              >
                {initializing && selectedMethod === 'generate' ? 'Starting...' : 'Generate Fresh Data'}
              </Button>
            </div>
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