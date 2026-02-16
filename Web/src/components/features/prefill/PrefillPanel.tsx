import { useEffect, useCallback, useState, useRef } from 'react';
import { useTranslation } from 'react-i18next';
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
import { useSignalR } from '@contexts/SignalRContext';
import { SteamIcon } from '@components/ui/SteamIcon';
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
  const { t } = useTranslation();
  const hasExpiredRef = useRef(false);
  const gamesCacheRef = useRef<{
    sessionId: string | null;
    fetchedAt: number;
    ownedGames: OwnedGame[];
    cachedAppIds: number[];
    hasData: boolean;
  } | null>(null);
  const gamesCacheWindowMs = 5 * 60 * 1000;

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
  const { isAdmin } = useAuth();
  const isUserAuthenticated = isAdmin;

  // Main SignalR hub for system-level events (PrefillDefaultsChanged)
  const { on: onSignalR, off: offSignalR } = useSignalR();

  // Local UI state
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<number[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);
  const [cachedAppIds, setCachedAppIds] = useState<number[]>([]);
  const [isUsingGamesCache, setIsUsingGamesCache] = useState(false);

  // Prefill settings state
  const [selectedOS, setSelectedOS] = useState<string[]>(['windows', 'linux', 'macos']);
  const [maxConcurrency, setMaxConcurrency] = useState<string>('auto');
  const [serverThreadCount, setServerThreadCount] = useState<number>(0);
  const [maxThreadLimit, setMaxThreadLimit] = useState<number | null>(null);

  // Load prefill defaults from server (reusable for initial load + SignalR refresh)
  const loadPrefillDefaults = useCallback(async () => {
    try {
      const response = await fetch(`${API_BASE}/system/prefill-defaults`, {
        credentials: 'include'
      });
      if (response.ok) {
        const data = await response.json();
        if (data.operatingSystems && Array.isArray(data.operatingSystems)) {
          setSelectedOS(data.operatingSystems);
        }
        if (typeof data.serverThreadCount === 'number' && data.serverThreadCount > 0) {
          setServerThreadCount(data.serverThreadCount);
        }
        const limit: number | null = data.maxThreadLimit ?? null;
        setMaxThreadLimit(limit);

        // Clamp concurrency to the guest thread limit so the dropdown
        // never selects a value that exceeds the allowed maximum
        let concurrency: string = data.maxConcurrency || 'auto';
        if (limit != null) {
          if (concurrency === 'max') {
            concurrency = String(limit);
          } else {
            const numeric = parseInt(concurrency, 10);
            if (!isNaN(numeric) && numeric > limit) {
              concurrency = String(limit);
            }
          }
        }
        setMaxConcurrency(concurrency);
      }
    } catch (err) {
      console.error('[PrefillPanel] Failed to load prefill defaults:', err);
    }
  }, []);

  // Load on mount
  useEffect(() => {
    loadPrefillDefaults();
  }, [loadPrefillDefaults]);

  // Listen for PrefillDefaultsChanged (admin changes OS/concurrency),
  // GuestPrefillConfigChanged (admin changes system-wide guest thread limit),
  // and UserPreferencesUpdated (admin changes per-session thread limit) — re-fetch
  // to get session-resolved effective maxThreadLimit
  useEffect(() => {
    onSignalR('PrefillDefaultsChanged', loadPrefillDefaults);
    onSignalR('GuestPrefillConfigChanged', loadPrefillDefaults);
    onSignalR('UserPreferencesUpdated', loadPrefillDefaults);
    return () => {
      offSignalR('PrefillDefaultsChanged', loadPrefillDefaults);
      offSignalR('GuestPrefillConfigChanged', loadPrefillDefaults);
      offSignalR('UserPreferencesUpdated', loadPrefillDefaults);
    };
  }, [onSignalR, offSignalR, loadPrefillDefaults]);

  // Save prefill defaults to API
  const savePrefillDefaults = useCallback(async (os?: string[], concurrency?: string) => {
    try {
      const body: Record<string, unknown> = {};
      if (os !== undefined) body.operatingSystems = os;
      if (concurrency !== undefined) body.maxConcurrency = concurrency;

      await fetch(`${API_BASE}/system/prefill-defaults`, {
        method: 'PATCH',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body)
      });
    } catch (err) {
      console.error('[PrefillPanel] Failed to save prefill defaults:', err);
    }
  }, []);

  // Wrapper setters that also persist to API
  const handleOSChange = useCallback((newOS: string[]) => {
    setSelectedOS(newOS);
    savePrefillDefaults(newOS, undefined);
  }, [savePrefillDefaults]);

  const handleConcurrencyChange = useCallback((newConcurrency: string) => {
    setMaxConcurrency(newConcurrency);
    savePrefillDefaults(undefined, newConcurrency);
  }, [savePrefillDefaults]);

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
          addLog('success', t('prefill.log.loginSuccess'));
          break;
        case 'CredentialsRequired':
          authActions.resetAuthForm();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.credentialsRequired'));
          break;
        case 'TwoFactorRequired':
          trigger2FAPrompt();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.twoFactorRequired'));
          break;
        case 'EmailCodeRequired':
          triggerEmailPrompt();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.emailCodeRequired'));
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
    hasExpiredRef.current = false;

    const interval = setInterval(() => {
      // Ensure UTC interpretation for timestamps without timezone suffix
      const expiresAtStr = signalR.session!.expiresAt;
      const expiresAtUtc = expiresAtStr.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(expiresAtStr)
        ? expiresAtStr
        : expiresAtStr + 'Z';
      const remaining = Math.max(
        0,
        Math.floor((new Date(expiresAtUtc).getTime() - Date.now()) / 1000)
      );
      signalR.setTimeRemaining(remaining);

      if (remaining <= 0 && !hasExpiredRef.current) {
        hasExpiredRef.current = true;
        signalR.setError(t('prefill.errors.sessionExpired'));
        signalR.setTimeRemaining(0);
        signalR.setIsLoggedIn(false);
        signalR.setSession(prev => prev ? { ...prev, status: 'Expired' } : prev);
      }
    }, 1000);

    return () => clearInterval(interval);
  }, [signalR.session, signalR.setSession, signalR.setIsLoggedIn, signalR.setTimeRemaining, signalR.setError, t]);

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

      if (maxConcurrency === 'max' && serverThreadCount > 0) {
        requestBody.maxConcurrency = serverThreadCount;
      } else if (maxConcurrency !== 'auto') {
        const parsed = parseInt(maxConcurrency, 10);
        if (!isNaN(parsed) && parsed > 0) {
          requestBody.maxConcurrency = parsed;
        }
      }

      const response = await fetch(`${API_BASE}/prefill-daemon/sessions/${sessionId}/prefill`, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(requestBody)
      });

      if (!response.ok) {
        const error = await response.json().catch(() => ({ message: t('prefill.errors.requestFailed') }));
        throw new Error(error.message || t('prefill.errors.httpStatus', { status: response.status }));
      }

      return response.json();
    },
    [selectedOS, maxConcurrency, serverThreadCount, signalR.isCancelling]
  );

  const loadGames = useCallback(
    async (force = false) => {
      if (!signalR.session) return;

      setIsLoadingGames(true);
      try {
        const gamesCache = gamesCacheRef.current;
        const isCacheFresh = !force
          && gamesCache
          && gamesCache.hasData
          && gamesCache.sessionId === signalR.session.id
          && Date.now() - gamesCache.fetchedAt < gamesCacheWindowMs;

        if (isCacheFresh) {
          setOwnedGames(gamesCache.ownedGames);
          setCachedAppIds(gamesCache.cachedAppIds);
          setIsUsingGamesCache(true);
          return;
        }

        setIsUsingGamesCache(false);

        // Fetch owned games via direct API call
        const gamesResponse = await fetch(
          `${API_BASE}/prefill-daemon/sessions/${signalR.session.id}/games`,
          { credentials: 'include' }
        );
        if (!gamesResponse.ok) {
          throw new Error(`Failed to get games: HTTP ${gamesResponse.status}`);
        }
        const games = await gamesResponse.json();
        setOwnedGames(games || []);
        addLog('info', t('prefill.log.foundGames', { count: games?.length || 0 }));

        // Get cached apps via ApiService and verify against Steam manifests
        const cachedApps = await ApiService.getPrefillCachedApps();
        let cachedIds = cachedApps.map(a => a.appId);

        if (cachedIds.length > 0) {
          try {
            const cacheStatus = await ApiService.getPrefillCacheStatus(signalR.session.id, cachedIds);
            cachedIds = cacheStatus?.upToDateAppIds?.length ? cacheStatus.upToDateAppIds : [];
          } catch (cacheStatusError) {
            console.warn('Failed to check cache status, clearing cached list:', cacheStatusError);
            cachedIds = [];
          }
        }

        setCachedAppIds(cachedIds);
        gamesCacheRef.current = {
          sessionId: signalR.session.id,
          fetchedAt: Date.now(),
          ownedGames: games || [],
          cachedAppIds: cachedIds,
          hasData: true
        };
        if (cachedIds.length > 0) {
          addLog('info', t('prefill.log.gamesCached', { count: cachedIds.length }));
        }
      } catch (err) {
        console.error('Failed to load games:', err);
        addLog('error', t('prefill.log.failedLoadLibrary'));
      } finally {
        setIsLoadingGames(false);
      }
    },
    [signalR.session, addLog, t]
  );

  const executeCommand = useCallback(
    async (commandType: CommandType) => {
      if (!signalR.session || !signalR.hubConnection.current) return;
      if (signalR.session.status !== 'Active' || signalR.timeRemaining <= 0) {
        signalR.setError(t('prefill.errors.sessionExpired'));
        addLog('warning', t('prefill.errors.sessionExpired'));
        return;
      }

      setIsExecuting(true);

      try {
        switch (commandType) {
          case 'prefill': {
            if (selectedAppIds.length === 0) {
              addLog('warning', t('prefill.log.noGamesSelected'));
              break;
            }
            signalR.expectedAppCountRef.current = selectedAppIds.length;
            addLog('download', t('prefill.log.startingPrefillSelected', { count: selectedAppIds.length }));
            const result = await callPrefillApi(signalR.session.id, {});
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'prefill-all': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', t('prefill.log.startingPrefillAll'));
            const result = await callPrefillApi(signalR.session.id, { all: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'prefill-recent': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', t('prefill.log.startingPrefillRecent'));
            const result = await callPrefillApi(signalR.session.id, { recent: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'prefill-recent-purchased': {
            signalR.expectedAppCountRef.current = 0;
            addLog('download', t('prefill.log.startingPrefillRecentPurchased'));
            const result = await callPrefillApi(signalR.session.id, { recentlyPurchased: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'prefill-top': {
            signalR.expectedAppCountRef.current = 50;
            addLog('download', t('prefill.log.startingPrefillTop'));
            const result = await callPrefillApi(signalR.session.id, { top: 50 });
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'prefill-force': {
            signalR.expectedAppCountRef.current = selectedAppIds.length || 0;
            addLog('download', t('prefill.log.startingPrefillForce'));
            const result = await callPrefillApi(signalR.session.id, { force: true });
            if (!result?.success) {
              addLog('error', result?.errorMessage || t('prefill.log.prefillFailed'));
            }
            break;
          }
          case 'select-apps': {
            addLog('progress', t('prefill.log.loadingGameLibrary'));
            setShowGameSelection(true);
            await loadGames();
            break;
          }
          case 'clear-temp': {
            addLog('info', t('prefill.log.clearingTempCache'));
            try {
              await signalR.hubConnection.current.invoke('ClearCache', signalR.session.id);
              addLog('success', t('prefill.log.tempCacheCleared'));
            } catch (err) {
              const errorMessage = err instanceof Error ? err.message : t('prefill.log.failedClearCache');
              addLog('error', errorMessage);
            }
            break;
          }
          case 'clear-cache-data': {
            addLog('info', t('prefill.log.clearingCacheDb'));
            try {
              const result = await ApiService.clearAllPrefillCache();
              addLog('success', result.message || t('prefill.log.cacheDbCleared'));
              setCachedAppIds([]);
            } catch (err) {
              const errorMessage = err instanceof Error ? err.message : t('prefill.log.failedClearCacheDb');
              addLog('error', errorMessage);
            }
            break;
          }
        }
      } catch (err) {
        console.error('Command execution failed:', err);
        addLog('error', err instanceof Error ? err.message : t('prefill.log.commandFailed'));
      } finally {
        setIsExecuting(false);
      }
    },
    [signalR.session, signalR.hubConnection, signalR.expectedAppCountRef, signalR.timeRemaining, signalR.setError, callPrefillApi, selectedAppIds, addLog, loadGames, t]
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
    addLog('info', t('prefill.log.cancellingPrefill'));

    try {
      await signalR.hubConnection.current.invoke('CancelPrefill', signalR.session.id);
    } catch (err) {
      signalR.isCancelling.current = false;
      addLog('error', t('prefill.log.failedCancelPrefill'));
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
            credentials: 'include',
            headers: {
              'Content-Type': 'application/json'
            },
            body: JSON.stringify({ appIds })
          }
        );
        if (!response.ok) {
          throw new Error(`Failed to save selection: HTTP ${response.status}`);
        }
        addLog('success', t('prefill.log.selectedGames', { count: appIds.length }));
      } catch (err) {
        console.error('Failed to save selection:', err);
        addLog('error', t('prefill.log.failedSaveSelection'));
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
        error: t('prefill.errors.unableEstimateSize')
      });
    }
  }, [signalR.session, signalR.hubConnection, selectedAppIds, selectedOS]);

  const getConfirmationMessage = useCallback(
    (command: CommandType): { title: string; message: string } => {
      switch (command) {
        case 'prefill':
          return {
            title: t('prefill.confirm.downloadTitle'),
            message: t('prefill.confirm.downloadMessage', { count: selectedAppIds.length })
          };
        case 'prefill-all':
          return {
            title: t('prefill.confirm.downloadAllTitle'),
            message: t('prefill.confirm.downloadAllMessage')
          };
        case 'prefill-force':
          return {
            title: t('prefill.confirm.forceTitle'),
            message: t('prefill.confirm.forceMessage')
          };
        case 'prefill-recent':
          return {
            title: t('prefill.confirm.recentTitle'),
            message: t('prefill.confirm.recentMessage')
          };
        case 'prefill-recent-purchased':
          return {
            title: t('prefill.confirm.recentPurchasedTitle'),
            message: t('prefill.confirm.recentPurchasedMessage')
          };
        case 'prefill-top':
          return {
            title: t('prefill.confirm.topTitle'),
            message: t('prefill.confirm.topMessage')
          };
        case 'clear-cache-data':
          return {
            title: t('prefill.confirm.clearDbTitle'),
            message: t('prefill.confirm.clearDbMessage')
          };
        default:
          return { title: t('common.confirm'), message: t('prefill.confirm.defaultMessage') };
      }
    },
    [selectedAppIds, t]
  );

  const handleCommandClick = useCallback(
    (command: CommandType) => {
      const requiresConfirmation = [
        'prefill',
        'prefill-all',
        'prefill-recent',
        'prefill-recent-purchased',
        'prefill-top',
        'prefill-force',
        'clear-cache-data'
      ].includes(command);

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

  const isLoadingSession = signalR.isInitializing || signalR.isCreating;
  const isSessionActive = !!signalR.session && signalR.session.status === 'Active' && signalR.timeRemaining > 0;
  const isSessionExpired = !!signalR.session && !isSessionActive;

  const handleStartNewSession = useCallback(() => {
    setShowAuthModal(false);
    setShowGameSelection(false);
    setOwnedGames([]);
    setCachedAppIds([]);
    setSelectedAppIds([]);
    setIsUsingGamesCache(false);
    gamesCacheRef.current = null;
    signalR.setError(null);
    signalR.setSession(null);
    signalR.setIsLoggedIn(false);
    signalR.setTimeRemaining(0);
    signalR.createSession(clearLogs);
  }, [signalR, clearLogs]);

  // No session state - show start screen
  if (!signalR.session && !isLoadingSession) {
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
  if (isLoadingSession) {
    const status = signalR.isCreating ? 'creating' : 'checking';
    return <PrefillLoadingState status={status} />;
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
        isUsingCache={isUsingGamesCache}
        onRescan={() => loadGames(true)}
      />

      {/* Large Prefill Confirmation Dialog */}
      <PrefillConfirmModal
        pendingCommand={pendingConfirmCommand}
        estimatedSize={estimatedSize}
        onConfirm={handleConfirmCommand}
        onCancel={handleCancelConfirm}
        getConfirmationMessage={getConfirmationMessage}
      />

      {/* Session Expired Notice */}
      {isSessionExpired && (
        <div className="p-4 rounded-lg flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 bg-[color-mix(in_srgb,var(--theme-warning)_15%,transparent)] border border-[color-mix(in_srgb,var(--theme-warning)_30%,transparent)]">
          <div className="flex items-start gap-3">
            <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-warning)]" />
            <div>
              <p className="font-medium text-sm text-[var(--theme-warning-text)]">
                {t('prefill.sessionExpired.title')}
              </p>
              <p className="text-sm text-themed-muted">
                {t('prefill.sessionExpired.message')}
              </p>
            </div>
          </div>
          <Button
            variant="filled"
            color="blue"
            onClick={handleStartNewSession}
            className="flex-shrink-0"
          >
            {t('prefill.sessionExpired.startNew')}
          </Button>
        </div>
      )}

      {/* Header Bar */}
      <div className="flex flex-col sm:flex-row items-start sm:items-center justify-between gap-4 p-4 rounded-lg bg-[var(--theme-bg-secondary)] border border-[var(--theme-border-primary)]">
        <div className="flex items-center gap-4">
          <div className="w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0 bg-[var(--theme-steam)]">
            <SteamIcon size={24} className="text-white" />
          </div>
          <div>
            <h1 className="text-xl font-bold text-themed-primary">{t('prefill.title')}</h1>
            <p className="text-sm text-themed-muted">{t('prefill.subtitle')}</p>
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
          {!isSessionExpired && (
            <Button
              variant="outline"
              size="sm"
              onClick={handleEndSession}
              className="flex-shrink-0 border-[color-mix(in_srgb,var(--theme-error)_40%,transparent)] text-[var(--theme-error)]"
            >
              <X className="h-4 w-4" />
              <span className="hidden sm:inline">{t('prefill.endSession')}</span>
            </Button>
          )}
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
            <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
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
                    {signalR.isLoggedIn
                      ? t('prefill.auth.loggedIn')
                      : t('prefill.auth.loginRequired')}
                  </p>
                  <p className="text-sm text-themed-muted">
                    {signalR.isLoggedIn
                      ? t('prefill.auth.canUsePrefill')
                      : t('prefill.auth.authenticateToAccess')}
                  </p>
                </div>
              </div>

              {!signalR.isLoggedIn && !isSessionExpired && (
                <Button
                  variant="filled"
                  onClick={handleOpenAuthModal}
                  className="flex-shrink-0 w-full sm:w-auto"
                >
                  <SteamIcon size={18} />
                  {t('prefill.auth.loginToSteam')}
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
          {signalR.prefillProgress && isSessionActive && (
            <PrefillProgressCard progress={signalR.prefillProgress} onCancel={handleCancelPrefill} />
          )}

          {/* Command Buttons */}
          <PrefillCommandButtons
            isLoggedIn={signalR.isLoggedIn}
            isExecuting={isExecuting}
            isPrefillActive={signalR.isPrefillActive}
            isSessionActive={isSessionActive}
            isUserAuthenticated={isUserAuthenticated}
            selectedAppIds={selectedAppIds}
            selectedOS={selectedOS}
            maxConcurrency={maxConcurrency}
            serverThreadCount={serverThreadCount}
            maxThreadLimit={maxThreadLimit}
            onCommandClick={handleCommandClick}
            onSelectedOSChange={handleOSChange}
            onMaxConcurrencyChange={handleConcurrencyChange}
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
                <h3 className="text-base font-semibold text-themed-primary">{t('prefill.activityLog.title')}</h3>
                <p className="text-xs text-themed-muted">{t('prefill.activityLog.subtitle')}</p>
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
