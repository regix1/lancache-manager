import React, { useState, useEffect } from 'react';
import { Key, Eye, Loader2, Shield, Database, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import authService from '@services/auth.service';
import { useGuestConfig } from '@contexts/GuestConfigContext';
import { useSignalR } from '@contexts/SignalRContext';
import { useTranslation } from 'react-i18next';

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
  const { t } = useTranslation();
  const { guestDurationHours, guestModeLocked: contextGuestModeLocked } = useGuestConfig();
  const { on, off } = useSignalR();
  const [apiKey, setApiKey] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [dataAvailable, setDataAvailable] = useState(false);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);

  // Local state for guest mode lock - synced via SignalR for fast updates
  const [localGuestModeLocked, setLocalGuestModeLocked] = useState(contextGuestModeLocked);

  // Use local state but sync with context
  const guestModeLocked = localGuestModeLocked;

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
    // Clear local auth state to ensure a clean slate
    // This prevents periodic checks from interfering while user is typing
    authService.clearAuth();
  }, []); // Run once on mount

  useEffect(() => {
    if (allowGuestMode) {
      checkDataAvailability();
    }
  }, [allowGuestMode]);

  // Subscribe to SignalR database reset progress events (no polling - SignalR only)
  useEffect(() => {
    const handleDatabaseResetProgress = (event: {
      isProcessing?: boolean;
      percentComplete?: number;
      message?: string;
      status?: string;
    }) => {
      const statusLower = (event.status || '').toLowerCase();
      const isComplete = statusLower === 'completed';
      const isError = statusLower === 'failed';

      if (isComplete) {
        setResetStatus({
          isResetting: false,
          percentComplete: 100,
          message: event.message || t('modals.auth.databaseReset.completed'),
          status: 'completed'
        });
        setResetJustCompleted(true);
        setTimeout(() => setResetJustCompleted(false), 5000);
      } else if (isError) {
        setResetStatus({
          isResetting: false,
          percentComplete: 0,
          message: event.message || t('modals.auth.databaseReset.failed'),
          status: 'failed'
        });
        setResetJustCompleted(true);
        setTimeout(() => setResetJustCompleted(false), 5000);
      } else {
        setResetStatus({
          isResetting: true,
          percentComplete: event.percentComplete || 0,
          message: event.message || t('modals.auth.databaseReset.resetting'),
          status: event.status || 'running'
        });
      }
    };

    on('DatabaseResetProgress', handleDatabaseResetProgress);

    return () => {
      off('DatabaseResetProgress', handleDatabaseResetProgress);
    };
  }, [on, off]);

  // Subscribe directly to GuestModeLockChanged for fast updates
  useEffect(() => {
    const handleGuestModeLockChanged = (event: { isLocked: boolean }) => {
      setLocalGuestModeLocked(event.isLocked);
    };

    on('GuestModeLockChanged', handleGuestModeLockChanged);

    return () => {
      off('GuestModeLockChanged', handleGuestModeLockChanged);
    };
  }, [on, off]);

  // Sync with context when it changes (initial load)
  useEffect(() => {
    setLocalGuestModeLocked(contextGuestModeLocked);
  }, [contextGuestModeLocked]);

  const checkDataAvailability = async () => {
    setCheckingDataAvailability(true);
    try {
      // Use the public auth status endpoint for this check.
      // /api/system/setup requires a guest/admin session and will 401 before guest mode is started.
      const authCheck = await authService.checkAuth();
      const hasData = Boolean(authCheck.hasDataLoaded || authCheck.hasData);
      setDataAvailable(hasData);
      return hasData;
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
      setAuthError(t('modals.auth.errors.apiKeyRequired'));
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
    // Check if guest mode is locked first
    if (guestModeLocked) {
      setAuthError(t('modals.auth.errors.guestModeDisabled'));
      return;
    }

    const hasData = await checkDataAvailability();
    if (!hasData) {
      setAuthError(t('modals.auth.errors.guestModeNoData'));
      return;
    }

    try {
      await authService.startGuestMode();
      onAuthChanged?.();
      setTimeout(() => onAuthComplete(), 1000);
    } catch (err: unknown) {
      // Handle case where backend rejects (e.g., locked after button click)
      const message = err instanceof Error ? err.message : t('modals.auth.errors.failedToStartGuest');
      setAuthError(message.includes('disabled') ? message : t('modals.auth.errors.guestModeUnavailable'));
    }
  };

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
      <div className="relative z-10 w-full max-w-xl rounded-xl border overflow-hidden bg-themed-secondary border-themed-primary">
        {/* Header */}
        <div className="px-8 py-5 border-b flex items-center justify-between border-themed-secondary">
          <div className="flex items-center gap-2">
            <Shield className="w-5 h-5 text-primary" />
            <span className="font-semibold text-themed-primary">{title}</span>
          </div>
        </div>

        {/* Content */}
        <div className="p-8">
          {/* Database Reset Status Banner */}
          {(resetStatus.isResetting || resetJustCompleted) && (
            <div
              className={`mb-6 p-4 rounded-lg border ${
                resetJustCompleted
                  ? 'bg-success border-success'
                  : 'bg-warning border-warning'
              }`}
            >
              <div className="flex items-center gap-3">
                {resetJustCompleted ? (
                  <CheckCircle className="w-5 h-5 flex-shrink-0 text-success" />
                ) : (
                  <Database className="w-5 h-5 flex-shrink-0 animate-pulse text-warning" />
                )}
                <div className="flex-1 min-w-0">
                  <p
                    className={`font-medium text-sm ${
                      resetJustCompleted ? 'text-success-text' : 'text-warning-text'
                    }`}
                  >
                    {resetJustCompleted
                      ? t('modals.auth.databaseReset.complete')
                      : t('modals.auth.databaseReset.inProgress')}
                  </p>
                  <p
                    className={`text-xs mt-1 opacity-90 ${
                      resetJustCompleted ? 'text-success-text' : 'text-warning-text'
                    }`}
                  >
                    {resetJustCompleted
                      ? t('modals.auth.databaseReset.canLoginNow')
                      : resetStatus.message || t('modals.auth.databaseReset.pleaseWait')}
                  </p>
                  {resetStatus.isResetting && resetStatus.percentComplete > 0 && (
                    <div className="mt-2">
                      <div className="h-1.5 rounded-full overflow-hidden bg-themed-tertiary">
                        <div
                          className="h-full rounded-full transition-all duration-300 bg-warning"
                          style={{ width: `${resetStatus.percentComplete}%` }}
                        />
                      </div>
                      <p className="text-xs mt-1 text-right text-warning-text opacity-80">
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
                <span
                  className={`text-sm ${guestModeLocked ? 'text-error' : 'text-themed-muted'}`}
                >
                  {guestModeLocked
                    ? t('modals.auth.guestMode.disabled')
                    : t('modals.auth.guestMode.available', { count: guestDurationHours })}
                </span>
              </>
            )}
          </p>

          {/* API Key Form */}
          <div className="space-y-4">
            <div>
              <label className="block text-sm font-medium text-themed-primary mb-2">{t('modals.auth.labels.apiKey')}</label>
              <input
                type="text"
                value={apiKey}
                onChange={(e) => setApiKey(e.target.value)}
                onKeyDown={(e) => {
                  if (e.key === 'Enter' && apiKey.trim() && !resetStatus.isResetting) {
                    handleAuthenticate();
                  }
                }}
                placeholder={resetStatus.isResetting ? t('modals.auth.placeholders.waitForReset') : t('modals.auth.placeholders.enterApiKey')}
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
                {resetStatus.isResetting ? t('modals.auth.actions.pleaseWait') : authenticating ? t('modals.auth.actions.authenticating') : t('modals.auth.actions.authenticate')}
              </Button>

              {/* Show guest mode divider and button if allowed (disabled when locked) */}
              {allowGuestMode && (
                <>
                  <div className="flex items-center gap-4">
                    <div className="flex-1 h-px bg-themed-border-secondary" />
                    <span className="text-xs text-themed-muted">{t('modals.auth.labels.or')}</span>
                    <div className="flex-1 h-px bg-themed-border-secondary" />
                  </div>

                  <Button
                    variant="default"
                    leftSection={<Eye className="w-4 h-4" />}
                    onClick={handleStartGuestMode}
                    disabled={authenticating || checkingDataAvailability || !dataAvailable || resetStatus.isResetting || guestModeLocked}
                    fullWidth
                    title={
                      guestModeLocked
                        ? t('modals.auth.guestMode.disabledTitle')
                        : !dataAvailable
                        ? t('modals.auth.guestMode.noDataTitle')
                        : t('modals.auth.guestMode.viewDataTitle', { count: guestDurationHours })
                    }
                  >
                    {guestModeLocked
                      ? t('modals.auth.guestMode.disabledButton')
                      : !dataAvailable
                      ? t('modals.auth.guestMode.noDataButton')
                      : t('modals.auth.guestMode.continueButton', { count: guestDurationHours })}
                  </Button>
                </>
              )}
            </div>
          </div>

          {/* API Key Help */}
          <div className="mt-6 p-4 rounded-lg border bg-info border-info text-info-text">
            <p className="text-sm">
              <strong>{t('modals.auth.help.title')}</strong>
              <br />
              {t('modals.auth.help.description')}
            </p>
          </div>

          {authError && (
            <div className="mt-4 p-4 rounded-lg border bg-error border-error text-error-text">
              <p className="text-sm">{authError}</p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default AuthenticationModal;
