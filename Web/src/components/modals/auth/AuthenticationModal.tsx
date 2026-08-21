import React, { useState, useEffect } from 'react';
import { Key, Eye, Shield, Database, CheckCircle } from 'lucide-react';
import { Button } from '@components/ui/Button';
import CredentialFields from '@components/ui/CredentialFields';
import type { CredentialField } from '@components/ui/CredentialFields.types';
import LoadingSpinner from '@components/common/LoadingSpinner';
import authService from '@services/auth.service';
import { useAuth } from '@contexts/useAuth';
import { useGuestConfig } from '@contexts/useGuestConfig';
import { useSetupStatus } from '@contexts/useSetupStatus';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useTranslation } from 'react-i18next';
import { formatPercent } from '@utils/formatters';
import { getErrorMessage } from '@utils/error';

interface DatabaseResetStatus {
  isResetting: boolean;
  percentComplete: number;
  message: string;
  status: string;
}

interface AuthenticationModalProps {
  onAuthComplete: () => void;
  title?: string;
  subtitle?: string;
  allowGuestMode?: boolean;
}

const AuthenticationModal: React.FC<AuthenticationModalProps> = ({
  onAuthComplete,
  title,
  subtitle,
  allowGuestMode = true
}) => {
  const { t } = useTranslation();
  const { startGuestSession: authStartGuest, login: authLogin, authenticationEnabled } = useAuth();
  const { guestDurationHours, guestModeLocked: contextGuestModeLocked } = useGuestConfig();
  const { setupStatus, isSetupStatusKnown } = useSetupStatus();
  const { on, off } = useSignalR();
  const [apiKey, setApiKey] = useState('');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [authenticating, setAuthenticating] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  // Set only when the server refuses the credentials, never when guest mode fails or the network
  // does, because it decides whether the rotation notice is shown.
  const [signInRefused, setSignInRefused] = useState(false);
  // Null until a status call actually answers. A failed or timed-out check used to store false,
  // which is the same value as "the server said there are no download rows", and that disabled
  // the guest button on an installation that had data. Same unread-vs-false split as
  // accountExists / installationUnclaimed below.
  const [dataAvailable, setDataAvailable] = useState<boolean | null>(null);
  const [checkingDataAvailability, setCheckingDataAvailability] = useState(false);

  // Local state for guest mode lock - synced via SignalR for fast updates
  const [localGuestModeLocked, setLocalGuestModeLocked] = useState(contextGuestModeLocked);

  // Use local state but sync with context
  const guestModeLocked = localGuestModeLocked;

  // The server refuses a guest session while authentication is on and no account exists yet, so the
  // button is never offered in that state. An unknown account flag is a different thing: it is a
  // setup status nobody has managed to read, which is what a failed or timed-out status call leaves
  // behind (SetupStatusContext.tsx, UNREAD_SETUP_STATUS). Counting that as unclaimed disabled this
  // button on an installation that has accounts, leaving a sign-in form and no other way forward
  // when the credentials were the thing in doubt. The server still turns a genuinely unclaimed
  // guest away by itself. With authentication off the installation is account-less on purpose, so
  // the flag says nothing there and the button stays as it was.
  const installationUnclaimed = authenticationEnabled && setupStatus?.accountExists === false;
  // isCompleted is false on the unread placeholder too, so only a status call that actually
  // answered can hide guest for an unfinished wizard. StartGuest refuses that state.
  const setupKnownIncomplete = isSetupStatusKnown && setupStatus?.isCompleted === false;
  const offerGuest = allowGuestMode && !installationUnclaimed && !setupKnownIncomplete;

  // Database reset status
  const [resetStatus, setResetStatus] = useState<DatabaseResetStatus>({
    isResetting: false,
    percentComplete: 0,
    message: '',
    status: ''
  });
  const [resetJustCompleted, setResetJustCompleted] = useState(false);

  // Note: Auth state is managed by the session system
  // No need to manually clear auth on mount

  useEffect(() => {
    if (offerGuest) {
      void checkDataAvailability();
    }
  }, [offerGuest]);

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
    // eslint-disable-next-line react-hooks/exhaustive-deps
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

  const checkDataAvailability = async (): Promise<boolean | null> => {
    setCheckingDataAvailability(true);
    try {
      // Use the public auth status endpoint for this check. /api/system/setup is anonymous too, but
      // it answers with setup progress rather than whether any download rows exist.
      const authCheck = await authService.checkAuth();
      if (authCheck.reachable === false) {
        return null;
      }
      const hasData = Boolean(authCheck.hasDataLoaded || authCheck.hasData);
      setDataAvailable(hasData);
      return hasData;
    } catch (error) {
      console.error('Failed to check data availability:', error);
      return null;
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
    setSignInRefused(false);

    try {
      // Use AuthContext login which awaits fetchAuth() to fully settle state
      // before returning, ensuring all downstream contexts react properly
      const result = await authLogin(apiKey, username.trim(), password);
      if (result.success) {
        onAuthComplete();
      } else {
        setAuthError(result.message || t('modals.auth.errors.authenticationFailed'));
        setSignInRefused(true);
      }
    } catch (error: unknown) {
      setAuthError(getErrorMessage(error) || t('modals.auth.errors.authenticationFailed'));
    } finally {
      setAuthenticating(false);
    }
  };

  const credentialsFilled = apiKey.trim() !== '' && username.trim() !== '' && password !== '';

  const handleCredentialChange = (field: CredentialField, value: string) => {
    if (field === 'apiKey') {
      setApiKey(value);
    } else if (field === 'username') {
      setUsername(value);
    } else {
      setPassword(value);
    }
  };

  const handleStartGuestMode = async () => {
    if (guestModeLocked) {
      setAuthError(t('modals.auth.errors.guestModeDisabled'));
      return;
    }

    const hasData = await checkDataAvailability();
    if (hasData === false) {
      setAuthError(t('modals.auth.errors.guestModeNoData'));
      return;
    }

    try {
      // Use AuthContext startGuestSession which awaits fetchAuth() to fully
      // settle state before returning, ensuring all downstream contexts
      // (DashboardDataContext, RefreshRateContext, etc.) react properly
      const result = await authStartGuest();
      if (result.success) {
        onAuthComplete();
      } else {
        setAuthError(result.message || t('modals.auth.errors.guestModeUnavailable'));
      }
    } catch (err: unknown) {
      const message = getErrorMessage(err) || t('modals.auth.errors.failedToStartGuest');
      setAuthError(
        message.includes('disabled') ? message : t('modals.auth.errors.guestModeUnavailable')
      );
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
      {/* dvh, not vh: on a phone vh includes the space the browser's own chrome occupies, so the
          card is sized taller than what the person can actually see and the sign-in button sits
          under the address bar. */}
      <div className="relative z-10 w-full max-w-xl themed-border-radius border overflow-hidden flex flex-col max-h-[calc(100dvh-2rem)] bg-themed-secondary border-themed-primary">
        {/* Header */}
        <div className="px-5 sm:px-8 py-4 sm:py-5 border-b flex items-center justify-between border-themed-secondary">
          <div className="flex items-center gap-2">
            <Shield className="w-5 h-5 text-primary" />
            <span className="font-semibold text-themed-primary">
              {title ?? t('modals.auth.defaultTitle')}
            </span>
          </div>
        </div>

        {/* Content. min-h-0 is what lets this shrink inside the flex column: without it a flex item
            refuses to go below its content height, the card overflows its own max-height, and the
            sign-in button is clipped away with no way to scroll to it. */}
        <div className="flex-1 min-h-0 overflow-y-auto p-5 sm:p-8">
          {/* Database Reset Status Banner */}
          {(resetStatus.isResetting || resetJustCompleted) && (
            <div
              className={`mb-6 p-4 rounded-lg border ${
                resetJustCompleted ? 'bg-success border-success' : 'bg-warning border-warning'
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
                          className="h-full rounded-full transition-[width] duration-300 bg-warning"
                          style={{ width: `${resetStatus.percentComplete}%` }}
                        />
                      </div>
                      <p className="text-xs mt-1 text-right text-warning-text opacity-80">
                        {formatPercent(resetStatus.percentComplete, 1)}
                      </p>
                    </div>
                  )}
                </div>
              </div>
            </div>
          )}

          <p className="text-themed-secondary text-center mb-6">
            {subtitle ?? t('modals.auth.defaultSubtitle')}
            {offerGuest && (
              <>
                <br />
                <span className={`text-sm ${guestModeLocked ? 'text-error' : 'text-themed-muted'}`}>
                  {guestModeLocked
                    ? t('modals.auth.guestMode.disabled')
                    : t('modals.auth.guestMode.available', { count: guestDurationHours })}
                </span>
              </>
            )}
          </p>

          {/* Credentials Form */}
          <div className="space-y-4">
            <CredentialFields
              apiKey={apiKey}
              username={username}
              password={password}
              onChange={handleCredentialChange}
              onSubmit={handleAuthenticate}
              disabled={authenticating || resetStatus.isResetting}
              apiKeyPlaceholder={
                resetStatus.isResetting
                  ? t('modals.auth.placeholders.waitForReset')
                  : t('modals.auth.placeholders.enterApiKey')
              }
              autoFocus={!resetStatus.isResetting}
            />

            <div className="flex flex-col gap-3">
              <Button
                variant="filled"
                color="primary"
                leftSection={
                  authenticating ? <LoadingSpinner inline size="sm" /> : <Key className="w-4 h-4" />
                }
                onClick={handleAuthenticate}
                disabled={authenticating || !credentialsFilled || resetStatus.isResetting}
                fullWidth
              >
                {resetStatus.isResetting
                  ? t('modals.auth.actions.pleaseWait')
                  : authenticating
                    ? t('modals.auth.actions.authenticating')
                    : t('modals.auth.actions.authenticate')}
              </Button>

              {/* Show guest mode divider and button if allowed (disabled when locked) */}
              {offerGuest && (
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
                    disabled={
                      authenticating ||
                      checkingDataAvailability ||
                      dataAvailable === false ||
                      resetStatus.isResetting ||
                      guestModeLocked
                    }
                    fullWidth
                    title={
                      guestModeLocked
                        ? t('modals.auth.guestMode.disabledTitle')
                        : dataAvailable === false
                          ? t('modals.auth.guestMode.noDataTitle')
                          : t('modals.auth.guestMode.viewDataTitle', { count: guestDurationHours })
                    }
                  >
                    {guestModeLocked
                      ? t('modals.auth.guestMode.disabledButton')
                      : dataAvailable === false
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

          {/* Rotating the API key ends every session at once, so after a rotation everyone arrives
              here together and reads the refusal above as a wrong password. The server answers every
              sign-in failure identically on purpose, so the key cannot be named as the cause - it is
              named as the possibility, in copy of its own. */}
          {signInRefused && (
            <div className="mt-4 p-4 rounded-lg border bg-warning border-warning text-warning-text">
              <p className="text-sm">
                <strong>{t('modals.auth.keyRotated.title')}</strong>
                <br />
                {t('modals.auth.keyRotated.description')}
              </p>
            </div>
          )}
        </div>
      </div>
    </div>
  );
};

export default AuthenticationModal;
