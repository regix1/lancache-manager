import React, { useState, useEffect } from 'react';
import { AlertTriangle, Key, Eye, Loader } from 'lucide-react';
import { Button } from '@components/ui/Button';
import authService from '@services/auth.service';

interface AuthenticationModalProps {
  onAuthComplete: () => void;
  onAuthChanged?: () => void;
  title?: string;
  subtitle?: string;
  allowGuestMode?: boolean;
}

const AuthenticationModal: React.FC<AuthenticationModalProps> = ({
  onAuthComplete,
  onAuthChanged,
  title = 'Authentication Required',
  subtitle = 'Please enter your API key to continue',
  allowGuestMode = true
}) => {
  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);

  useEffect(() => {
    if (allowGuestMode) {
      checkDataAvailability();
    }
  }, [allowGuestMode]);

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      const setupResponse = await fetch('/api/management/setup-status');
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
          setTimeout(() => onAuthComplete(), 1000);
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
    setTimeout(() => onAuthComplete(), 1000);
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
            {title}
          </h1>
          <p className="text-lg text-themed-secondary">
            {subtitle}
          </p>
        </div>

        {/* Content */}
        <div className="mb-8">
          <p className="text-themed-secondary text-center mb-6">
            Enter your API key for full management access, or continue as guest to view data for 6 hours:
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
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && apiKey.trim()) {
                    handleAuthenticate();
                  }
                }}
                placeholder="Enter your API key here..."
                className="w-full p-3 text-sm themed-input"
                disabled={authenticating}
                autoFocus
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

              {/* Only show guest mode divider and button if allowed */}
              {allowGuestMode && (
                <>
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
                    title={!dataAvailable ? 'No data available. Complete setup first.' : 'View data for 6 hours'}
                  >
                    {!dataAvailable ? 'Guest Mode (No Data Available)' : 'Continue as Guest (6 hours)'}
                  </Button>
                </>
              )}
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

          {authError && (
            <div className="mt-4 p-4 rounded-lg"
                 style={{
                   backgroundColor: 'var(--theme-error-bg)',
                   borderColor: 'var(--theme-error)',
                   color: 'var(--theme-error-text)'
                 }}>
              <p className="text-sm">{authError}</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default AuthenticationModal;
