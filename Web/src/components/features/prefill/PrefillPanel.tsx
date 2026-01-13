import { useEffect, useCallback, useState } from 'react';
import { Card, CardContent } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import { ActivityLog } from './ActivityLog';
import { GameSelectionModal, type OwnedGame } from './GameSelectionModal';
import { NetworkStatusSection } from './NetworkStatusSection';
import ApiService from '@services/api.service';
import { usePrefillContext } from '@contexts/PrefillContext';
import { useAuth } from '@contexts/AuthContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import authService from '@services/auth.service';
import { API_BASE } from '@utils/constants';

import { ScrollText, X, Timer, LogIn, CheckCircle2, AlertCircle } from 'lucide-react';

// Import extracted components
import { PrefillStartScreen } from './PrefillStartScreen';
import { PrefillLoadingState } from './PrefillLoadingState';
import { PrefillProgressCard } from './PrefillProgressCard';
import { PrefillCommandButtons } from './PrefillCommandButtons';
import { PrefillConfirmModal } from './PrefillConfirmModal';
import { CompletionBanner } from './CompletionBanner';
import { usePrefillSignalR } from './hooks/usePrefillSignalR';
import {
  type SteamAuthState,
  type PrefillPanelProps,
  type CommandType,
  formatTimeRemaining
} from './types';

export function PrefillPanel({ onSessionEnd }: PrefillPanelProps) {
  // Use context for log entries (persists across tab switches)
  const {
    logEntries,
    addLog,
    clearLogs,
    backgroundCompletion,
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    clearAllPrefillStorage
  } = usePrefillContext();

  // Check if user is authenticated (not guest) for auth-only features
  const { isAuthenticated, authMode } = useAuth();
  const isUserAuthenticated = isAuthenticated && authMode === 'authenticated';

  // Local UI state
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<number[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);
  const [cachedAppIds, setCachedAppIds] = useState<number[]>([]);

  // Prefill settings state
  const [selectedOS, setSelectedOS] = useState<string[]>(['windows', 'linux', 'macos']);
  const [maxConcurrency, setMaxConcurrency] = useState<string>('default');

  // Confirmation dialog state
  const [pendingConfirmCommand, setPendingConfirmCommand] = useState<CommandType | null>(null);
  const [estimatedSize, setEstimatedSize] = useState<{
    bytes: number;
    loading: boolean;
    error?: string;
    apps?: Array<{
      appId: number;
      name: string;
      downloadSize: number;
      isUnsupportedOs?: boolean;
      unavailableReason?: string;
    }>;
    message?: string;
  }>({ bytes: 0, loading: false });

  // Handle auth state changes from backend SignalR events
  const handleAuthStateChanged = useCallback(
    (newState: SteamAuthState) => {
      switch (newState) {
        case 'Authenticated':
          signalR.setIsLoggedIn(true);
          setShowAuthModal(false);
          authActions.resetAuthForm();
          addLog('success', 'Successfully logged in to Steam');
          break;
        case 'CredentialsRequired':
          authActions.resetAuthForm();
          setShowAuthModal(true);
          addLog('auth', 'Steam credentials required');
          break;
        case 'TwoFactorRequired':
          trigger2FAPrompt();
          setShowAuthModal(true);
          addLog('auth', 'Two-factor authentication required');
          break;
        case 'EmailCodeRequired':
          triggerEmailPrompt();
          setShowAuthModal(true);
          addLog('auth', 'Email verification code required');
          break;
        case 'NotAuthenticated':
          signalR.setIsLoggedIn(false);
          break;
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [addLog]
  );

  // SignalR hook - manages connection, session, and progress
  const signalR = usePrefillSignalR({
    onSessionEnd,
    addLog,
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    onAuthStateChanged: handleAuthStateChanged,
    clearAllPrefillStorage
  });

  // Steam auth hook for container-based authentication
  const {
    state: authState,
    actions: authActions,
    trigger2FAPrompt,
    triggerEmailPrompt
  } = usePrefillSteamAuth({
    sessionId: signalR.session?.id ?? null,
    hubConnection: signalR.hubConnection.current,
    onSuccess: () => setShowAuthModal(false),
    onError: () => {
      /* Keep modal open on error */
    },
    onDeviceConfirmationTimeout: () => setShowAuthModal(false)
  });

  // Timer for session countdown
  useEffect(() => {
    if (!signalR.session || signalR.session.status !== 'Active') return;

    const interval = setInterval(() => {
      const remaining = Math.max(
        0,
        Math.floor((new Date(signalR.session!.expiresAt).getTime() - Date.now()) / 1000)
      );
      signalR.setTimeRemaining(remaining);

      if (remaining <= 0) {
        signalR.setError('Session expired');
        handleEndSession();
      }
    }, 1000);

    return () => clearInterval(interval);
  }, [signalR.session]);

  // Helper to call prefill REST API
  const callPrefillApi = useCallback(
    async (
      sessionId: string,
      options: {
        all?: boolean;
        recent?: boolean;
        recentlyPurchased?: boolean;
        top?: number;
        force?: boolean;
      } = {}
    ) => {
      signalR.isCancelling.current = false;

      const requestBody: Record<string, unknown> = { ...options };

      if (selectedOS.length > 0 && selectedOS.length < 3) {
        requestBody.operatingSystems = selectedOS;
      }

      if (maxConcurrency !== 'default') {
        requestBody.maxConcurrency = parseInt(maxConcurrency, 10);
      }

      const response = await fetch(`${API_BASE}/prefill-daemon/sessions/${sessionId}/prefill`, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...authService.getAuthHeaders()
        },
        body: JSON.stringify(requestBody)
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: 'Prefill request failed' }));
        throw new Error(error.message || `HTTP ${response.status}`);
      }

      return response.json();
    },
    [selectedOS, maxConcurrency, signalR.isCancelling]
  );

  const executeCommand = useCallback(
    async (commandType: CommandType) => {
      if (!signalR.session || !signalR.hubConnection.current) return;

      setIsExecuting(true);

      try {
        switch (commandType) {
          case 'select-apps': {
            setIsLoadingGames(true);
            setShowGameSelection(true);

            try {
              // Fetch owned games via direct API call
              const gamesResponse = await fetch(
                `${API_BASE}/prefill-daemon/sessions/${signalR.session.id}/games`,
                { headers: authService.getAuthHeaders() }
              );
              if (!gamesResponse.ok) {
                throw new Error(`Failed to get games: HTTP ${gamesResponse.status}`);
              }
              const games = await gamesResponse.json();
              setOwnedGames(games || []);
              addLog('info', `Found ${games?.length || 0} owned games`);

              // Get cached apps via ApiService
              const cachedApps = await ApiService.getPrefillCachedApps();
              setCachedAppIds(cachedApps.map(a => a.appId));
              if (cachedApps.length > 0) {
                addLog('info', `${cachedApps.length} games already cached in lancache`);
              }
            } catch (err) {
              console.error('Failed to load games:', err);
              addLog('error', 'Failed to load game library');
            } finally {
              setIsLoadingGames(false);
            }
            break;
          }
          case 'prefill': {
            if (selectedAppIds.length === 0) {
              addLog('warning', 'No games selected. Use "Select Apps" to choose games for prefill first.');
              break;
            }
            signalR.expectedAppCountRef.current = selectedAppIds.length;
            addLog('download', `Starting prefill of ${selectedAppIds.length} selected apps...`);
            const result = await callPrefillApi(signalR.session.id, {});
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-all': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', 'Starting prefill of all owned games...');
            const result = await callPrefillApi(signalR.session.id, { all: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-recent': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', 'Starting prefill of recently played games...');
            const result = await callPrefillApi(signalR.session.id, { recent: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-recent-purchased': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', 'Starting prefill of recently purchased games...');
            const result = await callPrefillApi(signalR.session.id, { recentlyPurchased: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-top': {
            signalR.expectedAppCountRef.current = 50;
            addLog('download', 'Starting prefill of top 50 popular games...');
            const result = await callPrefillApi(signalR.session.id, { top: 50 });
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'prefill-force': {
            signalR.expectedAppCountRef.current = selectedAppIds.length || 0;
            addLog('download', 'Starting force prefill (re-downloading)...');
            const result = await callPrefillApi(signalR.session.id, { force: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || 'Prefill failed');
            }
            break;
          }
          case 'clear-temp': {
            addLog('info', 'Clearing temporary cache...');
            try {
              await signalR.hubConnection.current.invoke('ClearCache', signalR.session.id);
              addLog('success', 'Temporary cache cleared');
            } catch (err) {
              const errorMessage = err instanceof Error ? err.message : 'Failed to clear cache';
              addLog('error', errorMessage);
            }
            break;
          }
          case 'clear-cache-data': {
            addLog('info', 'Clearing prefill cache database...');
            try {
              const result = await ApiService.clearAllPrefillCache();
              addLog('success', result.message || 'Prefill cache database cleared successfully');
              setCachedAppIds([]);
            } catch (err) {
              const errorMessage = err instanceof Error ? err.message : 'Failed to clear cache database';
              addLog('error', errorMessage);
            }
            break;
          }
        }
      } catch (err) {
        console.error('Command execution failed:', err);
        addLog('error', err instanceof Error ? err.message : 'Command failed');
      } finally {
        setIsExecuting(false);
      }
    },
    [signalR.session, signalR.hubConnection, signalR.expectedAppCountRef, callPrefillApi, selectedAppIds, addLog]
  );

  const handleEndSession = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    try {
      await signalR.hubConnection.current.invoke('EndSession', signalR.session.id);
    } catch (err) {
      console.error('Failed to end session:', err);
    }
  }, [signalR.session, signalR.hubConnection]);

  const handleCancelLogin = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    try {
      await signalR.hubConnection.current.invoke('CancelLogin', signalR.session.id);
      setShowAuthModal(false);
      authActions.resetAuthForm();
    } catch (err) {
      console.error('Failed to cancel login:', err);
    }
  }, [signalR.session, signalR.hubConnection, authActions]);

  const handleCancelPrefill = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    signalR.isCancelling.current = true;
    addLog('info', 'Cancelling prefill operation...');

    try {
      await signalR.hubConnection.current.invoke('CancelPrefill', signalR.session.id);
    } catch (err) {
      signalR.isCancelling.current = false;
      addLog('error', 'Failed to cancel prefill');
    }
  }, [signalR.session, signalR.hubConnection, signalR.isCancelling, addLog]);

  const handleOpenAuthModal = useCallback(() => {
    authActions.resetAuthForm();
    setShowAuthModal(true);
  }, [authActions]);

  const handleSaveGameSelection = useCallback(
    async (appIds: number[]) => {
      if (!signalR.session) return;

      setSelectedAppIds(appIds);
      setShowGameSelection(false);

      try {
        const response = await fetch(
          `${API_BASE}/prefill-daemon/sessions/${signalR.session.id}/selected-apps`,
          {
            method: 'POST',
            headers: {
              'Content-Type': 'application/json',
              ...authService.getAuthHeaders()
            },
            body: JSON.stringify({ appIds })
          }
        );
        if (!response.ok) {
          throw new Error(`Failed to save selection: HTTP ${response.status}`);
        }
        addLog('success', `Selected ${appIds.length} game${appIds.length !== 1 ? 's' : ''} for prefill`);
      } catch (err) {
        console.error('Failed to save selection:', err);
        addLog('error', 'Failed to save game selection');
      }
    },
    [signalR.session, addLog]
  );

  // Confirmation dialog logic
  const fetchEstimatedSize = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current || selectedAppIds.length === 0) return;

    setEstimatedSize({ bytes: 0, loading: true });

    try {
      const status = await signalR.hubConnection.current.invoke(
        'GetSelectedAppsStatus',
        signalR.session.id,
        selectedOS
      ) as {
        totalDownloadSize: number;
        message?: string;
        apps?: Array<{
          appId: number;
          name: string;
          downloadSize: number;
          isUnsupportedOs?: boolean;
          unavailableReason?: string;
        }>;
      };

      setEstimatedSize({
        bytes: status.totalDownloadSize || 0,
        loading: false,
        apps: status.apps?.map((a) => ({
          appId: a.appId,
          name: a.name,
          downloadSize: a.downloadSize,
          isUnsupportedOs: a.isUnsupportedOs,
          unavailableReason: a.unavailableReason
        })),
        message: status.message
      });
    } catch (err) {
      setEstimatedSize({
        bytes: 0,
        loading: false,
        error: 'Unable to estimate size'
      });
    }
  }, [signalR.session, signalR.hubConnection, selectedAppIds, selectedOS]);

  const getConfirmationMessage = useCallback(
    (command: CommandType): { title: string; message: string } => {
      switch (command) {
        case 'prefill':
          return {
            title: 'Confirm Download',
            message: `You are about to download ${selectedAppIds.length} game${selectedAppIds.length !== 1 ? 's' : ''}. This may take a while and use significant bandwidth.`
          };
        case 'prefill-all':
          return {
            title: 'Download All Games',
            message:
              'This will download ALL games in your Steam library. This could be hundreds of gigabytes and take many hours.'
          };
        case 'prefill-force':
          return {
            title: 'Force Re-download',
            message:
              'This will re-download games even if they are already cached. Use this if you suspect cache corruption.'
          };
        case 'clear-cache-data':
          return {
            title: 'Clear Cache Database',
            message:
              'This will remove all cache records from the database. Cached files will remain but tracking data will be lost.'
          };
        default:
          return { title: 'Confirm', message: 'Are you sure you want to proceed?' };
      }
    },
    [selectedAppIds]
  );

  const handleCommandClick = useCallback(
    (command: CommandType) => {
      const requiresConfirmation = ['prefill', 'prefill-all', 'prefill-force', 'clear-cache-data'].includes(command);

      if (requiresConfirmation) {
        setPendingConfirmCommand(command);
        if (command === 'prefill') {
          fetchEstimatedSize();
        }
      } else {
        executeCommand(command);
      }
    },
    [executeCommand, fetchEstimatedSize]
  );

  const handleConfirmCommand = useCallback(() => {
    if (pendingConfirmCommand) {
      executeCommand(pendingConfirmCommand);
      setPendingConfirmCommand(null);
      setEstimatedSize({ bytes: 0, loading: false });
    }
  }, [pendingConfirmCommand, executeCommand]);

  const handleCancelConfirm = useCallback(() => {
    setPendingConfirmCommand(null);
    setEstimatedSize({ bytes: 0, loading: false });
  }, []);

  // No session state - show start screen
  if (!signalR.session && !signalR.isInitializing) {
    return (
      <>
        <SteamAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          isPrefillMode={true}
          onCancelLogin={handleCancelLogin}
        />
        <PrefillStartScreen
          error={signalR.error}
          isConnecting={signalR.isConnecting}
          onCreateSession={() => signalR.createSession(clearLogs)}
        />
      </>
    );
  }

  // Loading/Creating state
  if (signalR.isInitializing) {
    return <PrefillLoadingState isInitializing={true} />;
  }

  // Active session - full interface
  return (
    <div className="space-y-4 animate-fade-in">
      {/* Steam Auth Modal */}
      <SteamAuthModal
        opened={showAuthModal}
        onClose={() => setShowAuthModal(false)}
        state={authState}
        actions={authActions}
        isPrefillMode={true}
        onCancelLogin={handleCancelLogin}
      />

      {/* Game Selection Modal */}
      <GameSelectionModal
        opened={showGameSelection}
        onClose={() => setShowGameSelection(false)}
        games={ownedGames}
        selectedAppIds={selectedAppIds}
        onSave={handleSaveGameSelection}
        isLoading={isLoadingGames}
        cachedAppIds={cachedAppIds}
      />

      {/* Large Prefill Confirmation Dialog */}
      <PrefillConfirmModal
        pendingCommand={pendingConfirmCommand}
        estimatedSize={estimatedSize}
        onConfirm={handleConfirmCommand}
        onCancel={handleCancelConfirm}
        getConfirmationMessage={getConfirmationMessage}
      />

      {/* Header Bar */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 p-4 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0 bg-[var(--theme-steam)]">
            <SteamIcon size={24} className="text-white" />
          </div>
          <div>
            <h1 className="text-xl font-bold text-themed-primary">Steam Prefill</h1>
            <p className="text-sm text-themed-muted">Pre-download games to your cache</p>
          </div>
        </div>

        <div className="flex items-center gap-3 w-full sm:w-auto">
          {/* Session Timer */}
          <div
            className={`flex items-center gap-2 px-4 py-2 rounded-lg flex-1 sm:flex-initial justify-center border ${
              signalR.timeRemaining < 600
                ? 'bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)] border-[color-mix(in_srgb,var(--theme-warning)_30%,transparent)]'
                : 'bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)]'
            }`}
          >
            <Timer
              className={`h-4 w-4 ${
                signalR.timeRemaining < 600 ? 'text-[var(--theme-warning)]' : 'text-[var(--theme-text-muted)]'
              }`}
            />
            <span
              className={`font-mono font-semibold tabular-nums ${
                signalR.timeRemaining < 600 ? 'text-[var(--theme-warning-text)]' : 'text-[var(--theme-text-primary)]'
              }`}
            >
              {formatTimeRemaining(signalR.timeRemaining)}
            </span>
          </div>

          {/* End Session Button */}
          <Button
            variant="outline"
            size="sm"
            onClick={handleEndSession}
            className="flex-shrink-0 border-[color-mix(in_srgb,var(--theme-error)_40%,transparent)] text-[var(--theme-error)]"
          >
            <X className="h-4 w-4" />
            <span className="hidden sm:inline">End Session</span>
          </Button>
        </div>
      </div>

      {/* Error Banner */}
      {signalR.error && (
        <div className="p-4 rounded-lg flex items-center gap-3 bg-[var(--theme-error-bg)] border border-[color-mix(in_srgb,var(--theme-error)_30%,transparent)]">
          <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-error)]" />
          <span className="text-[var(--theme-error-text)]">{signalR.error}</span>
        </div>
      )}

      {/* Main Content - Two Column Layout */}
      <div className="grid grid-cols-1 xl:grid-cols-3 gap-4">
        {/* Left Column - Controls */}
        <div className="xl:col-span-2 space-y-4">
          {/* Authentication Card */}
          <Card padding="md">
            <div className="flex items-center justify-between">
              <div className="flex items-center gap-3">
                <div
                  className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                    signalR.isLoggedIn
                      ? 'bg-[color-mix(in_srgb,var(--theme-success)_15%,transparent)]'
                      : 'bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)]'
                  }`}
                >
                  {signalR.isLoggedIn ? (
                    <CheckCircle2 className="h-5 w-5 text-[var(--theme-success)]" />
                  ) : (
                    <LogIn className="h-5 w-5 text-[var(--theme-warning)]" />
                  )}
                </div>
                <div>
                  <p className="font-medium text-themed-primary">
                    {signalR.isLoggedIn ? 'Logged In to Steam' : 'Steam Login Required'}
                  </p>
                  <p className="text-sm text-themed-muted">
                    {signalR.isLoggedIn
                      ? 'You can now use prefill commands'
                      : 'Authenticate to access your game library'}
                  </p>
                </div>
              </div>

              {!signalR.isLoggedIn && (
                <Button variant="filled" onClick={handleOpenAuthModal} className="flex-shrink-0">
                  <SteamIcon size={18} />
                  Login to Steam
                </Button>
              )}
            </div>
          </Card>

          {/* Network Status Card */}
          <NetworkStatusSection diagnostics={signalR.session?.networkDiagnostics} />

          {/* Background Completion Notification Banner */}
          {backgroundCompletion && !signalR.prefillProgress && (
            <CompletionBanner completion={backgroundCompletion} onDismiss={clearBackgroundCompletion} />
          )}

          {/* Download Progress Card */}
          {signalR.prefillProgress && (
            <PrefillProgressCard progress={signalR.prefillProgress} onCancel={handleCancelPrefill} />
          )}

          {/* Command Buttons */}
          <PrefillCommandButtons
            isLoggedIn={signalR.isLoggedIn}
            isExecuting={isExecuting}
            isPrefillActive={signalR.isPrefillActive}
            isUserAuthenticated={isUserAuthenticated}
            selectedAppIds={selectedAppIds}
            selectedOS={selectedOS}
            maxConcurrency={maxConcurrency}
            onCommandClick={handleCommandClick}
            onSelectedOSChange={setSelectedOS}
            onMaxConcurrencyChange={setMaxConcurrency}
          />
        </div>

        {/* Right Column - Activity Log */}
        <div className="xl:col-span-1">
          <Card padding="none" className="overflow-hidden">
            <div className="px-4 pt-4 pb-3 flex items-center gap-3 border-b border-[var(--theme-border-primary)]">
              <div className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-[color-mix(in_srgb,var(--theme-accent)_15%,transparent)]">
                <ScrollText className="h-4 w-4 text-[var(--theme-accent)]" />
              </div>
              <div>
                <h3 className="text-base font-semibold text-themed-primary">Activity Log</h3>
                <p className="text-xs text-themed-muted">Status updates and output</p>
              </div>
            </div>
            <CardContent className="p-0">
              <ActivityLog entries={logEntries} className="border-0 rounded-none" />
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
