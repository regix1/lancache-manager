import React, { useState, useEffect, useCallback } from 'react';
import { Key, Eye, Loader2, Shield, Database, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import authService from '@services/auth.service';
import { useGuestConfig } from '@contexts/GuestConfigContext';

interface DatabaseResetStatus {
  isResetting: boolean;
  percentComplete: number;
  message: string;
  status: string;
}

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
  const { guestDurationHours } = useGuestConfig();
  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);

  // Database reset status
  const [resetStatus, setResetStatus] = useState<DatabaseResetStatus>({
    isResetting: false,
    percentComplete: 0,
    message: '',
    status: ''
  });
  const [resetJustCompleted, setResetJustCompleted] = useState(false);

  // Clear auth state immediately when modal opens to prevent race conditions
  useEffect(() => {
    console.log('[AuthModal] Clearing stale auth state on mount');
    // Clear local auth state to ensure a clean slate
    // This prevents periodic checks from interfering while user is typing
    authService.clearAuth();
  }, []); // Run once on mount

  useEffect(() => {
    if (allowGuestMode) {
      checkDataAvailability();
    }
  }, [allowGuestMode]);

  // Check database reset status
  const checkResetStatus = useCallback(async () => {
    try {
      const response = await fetch('/api/database/reset-status');
      if (response.ok) {
        const data = await response.json();
        const isResetting = data.isProcessing || data.isResetting || false;
        const wasResetting = resetStatus.isResetting;

        setResetStatus({
          isResetting,
          percentComplete: data.percentComplete || 0,
          message: data.message || data.statusMessage || '',
          status: data.status || ''
        });

        // If reset just completed, show success message briefly
        if (wasResetting && !isResetting) {
          setResetJustCompleted(true);
          setTimeout(() => setResetJustCompleted(false), 5000);
        }
      }
    } catch (error) {
      // Silently fail - server might not be ready yet
      console.debug('[AuthModal] Failed to check reset status:', error);
    }
  }, [resetStatus.isResetting]);

  // Poll for database reset status
  useEffect(() => {
    // Check immediately on mount
    checkResetStatus();

    // Poll every 1 second while reset might be in progress
    const interval = setInterval(checkResetStatus, 1000);

    return () => clearInterval(interval);
  }, [checkResetStatus]);

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
    } catch (error: unknown) {
      setAuthError((error instanceof Error ? error.message : String(error)) || 'Authentication failed');
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
    setTimeout(() => onAuthComplete(), 1000);
  };

  return (
    <div
      className="fixed inset-0 z-[9999] flex items-center justify-center p-4"
      style={{ backgroundColor: 'var(--theme-bg-primary)' }}
    >
      {/* Stripe background pattern */}
      <div
        className="absolute inset-0 opacity-5"
        style={{
          backgroundImage: `repeating-linear-gradient(45deg, transparent, transparent 35px, var(--theme-text-primary) 35px, var(--theme-text-primary) 70px)`
        }}
      />

      {/* Main Card */}
      <div
        className="relative z-10 w-full max-w-xl rounded-xl border overflow-hidden"
        style={{
          backgroundColor: 'var(--theme-bg-secondary)',
          borderColor: 'var(--theme-border-primary)'
        }}
      >
        {/* Header */}
        <div
          className="px-8 py-5 border-b flex items-center justify-between"
          style={{ borderColor: 'var(--theme-border-secondary)' }}
        >
          <div className="flex items-center gap-2">
            <Shield className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
            <span className="font-semibold text-themed-primary">{title}</span>
          </div>
        </div>

        {/* Content */}
        <div className="p-8">
          {/* Database Reset Status Banner */}
          {(resetStatus.isResetting || resetJustCompleted) && (
            <div
              className="mb-6 p-4 rounded-lg border"
              style={{
                backgroundColor: resetJustCompleted
                  ? 'var(--theme-success-bg)'
                  : 'var(--theme-warning-bg)',
                borderColor: resetJustCompleted
                  ? 'var(--theme-success)'
                  : 'var(--theme-warning)'
              }}
            >
              <div className="flex items-center gap-3">
                {resetJustCompleted ? (
                  <CheckCircle
                    className="w-5 h-5 flex-shrink-0"
                    style={{ color: 'var(--theme-success)' }}
                  />
                ) : (
                  <Database
                    className="w-5 h-5 flex-shrink-0 animate-pulse"
                    style={{ color: 'var(--theme-warning)' }}
                  />
                )}
                <div className="flex-1 min-w-0">
                  <p
                    className="font-medium text-sm"
                    style={{
                      color: resetJustCompleted
                        ? 'var(--theme-success-text)'
                        : 'var(--theme-warning-text)'
                    }}
                  >
                    {resetJustCompleted
                      ? 'Database Reset Complete'
                      : 'Database Reset In Progress'}
                  </p>
                  <p
                    className="text-xs mt-1"
                    style={{
                      color: resetJustCompleted
                        ? 'var(--theme-success-text)'
                        : 'var(--theme-warning-text)',
                      opacity: 0.9
                    }}
                  >
                    {resetJustCompleted
                      ? 'You can now log in.'
                      : resetStatus.message || 'Please wait...'}
                  </p>
                  {resetStatus.isResetting && resetStatus.percentComplete > 0 && (
                    <div className="mt-2">
                      <div
                        className="h-1.5 rounded-full overflow-hidden"
                        style={{ backgroundColor: 'var(--theme-bg-tertiary)' }}
                      >
                        <div
                          className="h-full rounded-full transition-all duration-300"
                          style={{
                            width: `${resetStatus.percentComplete}%`,
                            backgroundColor: 'var(--theme-warning)'
                          }}
                        />
                      </div>
                      <p
                        className="text-xs mt-1 text-right"
                        style={{ color: 'var(--theme-warning-text)', opacity: 0.8 }}
                      >
                        {resetStatus.percentComplete.toFixed(1)}%
                      </p>
                    </div>
                  )}
                </div>
              </div>
            </div>
          )}

          <p className="text-themed-secondary text-center mb-6">
            {subtitle}
            {allowGuestMode && (
              <>
                <br />
                <span className="text-sm text-themed-muted">
                  Or continue as guest to view data for {guestDurationHours} hour
                  {guestDurationHours !== 1 ? 's' : ''}.
                </span>
              </>
            )}
          </p>

          {/* API Key Form */}
          <div className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-themed-primary mb-2">API Key</label>
              <input
                type="text"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && apiKey.trim() && !resetStatus.isResetting) {
                    handleAuthenticate();
                  }
                }}
                placeholder={resetStatus.isResetting ? 'Please wait for reset to complete...' : 'Enter your API key here...'}
                className="w-full p-3 text-sm themed-input"
                disabled={authenticating || resetStatus.isResetting}
                autoFocus={!resetStatus.isResetting}
              />
            </div>

            <div className="flex flex-col gap-3">
              <Button
                variant="filled"
                color="blue"
                leftSection={
                  authenticating ? (
                    <Loader2 className="w-4 h-4 animate-spin" />
                  ) : (
                    <Key className="w-4 h-4" />
                  )
                }
                onClick={handleAuthenticate}
                disabled={authenticating || !apiKey.trim() || resetStatus.isResetting}
                fullWidth
              >
                {resetStatus.isResetting ? 'Please Wait...' : authenticating ? 'Authenticating...' : 'Authenticate'}
              </Button>

              {/* Only show guest mode divider and button if allowed */}
              {allowGuestMode && (
                <>
                  <div className="flex items-center gap-4">
                    <div
                      className="flex-1 h-px"
                      style={{ backgroundColor: 'var(--theme-border-secondary)' }}
                    />
                    <span className="text-xs text-themed-muted">OR</span>
                    <div
                      className="flex-1 h-px"
                      style={{ backgroundColor: 'var(--theme-border-secondary)' }}
                    />
                  </div>

                  <Button
                    variant="default"
                    leftSection={<Eye className="w-4 h-4" />}
                    onClick={handleStartGuestMode}
                    disabled={authenticating || checkingDataAvailability || !dataAvailable || resetStatus.isResetting}
                    fullWidth
                    title={
                      !dataAvailable
                        ? 'No data available. Complete setup first.'
                        : `View data for ${guestDurationHours} hour${guestDurationHours !== 1 ? 's' : ''}`
                    }
                  >
                    {!dataAvailable
                      ? 'Guest Mode (No Data Available)'
                      : `Continue as Guest (${guestDurationHours} hour${guestDurationHours !== 1 ? 's' : ''})`}
                  </Button>
                </>
              )}
            </div>
          </div>

          {/* API Key Help */}
          <div
            className="mt-6 p-4 rounded-lg border"
            style={{
              backgroundColor: 'var(--theme-info-bg)',
              borderColor: 'var(--theme-info)',
              color: 'var(--theme-info-text)'
            }}
          >
            <p className="text-sm">
              <strong>Where to find your API key:</strong>
              <br />
              The API key was displayed when you first started the server. Check your server logs
              for "API Key:" or look in the <code>data/api_key.txt</code> file.
            </p>
          </div>

          {authError && (
            <div
              className="mt-4 p-4 rounded-lg border"
              style={{
                backgroundColor: 'var(--theme-error-bg)',
                borderColor: 'var(--theme-error)',
                color: 'var(--theme-error-text)'
              }}
            >
              <p className="text-sm">{authError}</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default AuthenticationModal;
