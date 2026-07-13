import { useEffect, useCallback, useState, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Card, CardContent } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import { ActivityLog } from './ActivityLog';
import { GameSelectionModal, type OwnedGame } from './GameSelectionModal';
import { NetworkStatusSection } from './NetworkStatusSection';
import ApiService from '@services/api.service';
import { usePrefillContext } from '@contexts/usePrefillContext';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { XboxIcon } from '@components/ui/XboxIcon';
import { API_BASE, STORAGE_KEYS } from '@utils/constants';
import { getErrorMessage } from '@utils/error';

import { ScrollText, X, Timer, LogIn, CheckCircle2, AlertCircle } from 'lucide-react';

import { useGameService } from '@contexts/useGameService';
import type { GameServiceId } from '@/types/gameService';

// Import extracted components
import { PrefillHomePage } from './PrefillHomePage';
import { PrefillLoadingState } from './PrefillLoadingState';
import { PrefillProgressCard } from './PrefillProgressCard';
import { PrefillCommandButtons } from './PrefillCommandButtons';
import { PrefillConfirmModal } from './PrefillConfirmModal';
import { CompletionBanner } from './CompletionBanner';
import { usePrefillSignalR } from './hooks/usePrefillSignalR';
import { prefillServiceConfig } from './hooks/prefillServiceConfig';
import { type PrefillPanelProps, type CommandType, formatTimeRemaining } from './types';
import type { DaemonAuthState } from '@/types/operations';

export function PrefillPanel({ onSessionEnd }: PrefillPanelProps) {
  const { selectedService, setSelectedService } = useGameService();
  const [pendingService, setPendingService] = useState<GameServiceId | null>(null);

  const hubPath = prefillServiceConfig(selectedService).hubPath;

  const handleServiceStart = useCallback(
    (serviceId: GameServiceId) => {
      if (serviceId !== selectedService) {
        setSelectedService(serviceId);
      }
      setPendingService(serviceId);
    },
    [selectedService, setSelectedService]
  );

  const handlePendingHandled = useCallback(() => {
    setPendingService(null);
  }, []);

  return (
    <ServicePrefillPanel
      key={selectedService}
      onSessionEnd={onSessionEnd}
      hubPath={hubPath}
      serviceId={selectedService}
      pendingService={pendingService}
      onPendingServiceHandled={handlePendingHandled}
      onServiceStart={handleServiceStart}
    />
  );
}

interface ServicePrefillPanelProps extends PrefillPanelProps {
  hubPath: string;
  serviceId: string;
  pendingService: GameServiceId | null;
  onPendingServiceHandled: () => void;
  onServiceStart: (serviceId: GameServiceId) => void;
}

function ServicePrefillPanel({
  onSessionEnd,
  hubPath,
  serviceId,
  pendingService,
  onPendingServiceHandled,
  onServiceStart
}: ServicePrefillPanelProps) {
  const { t } = useTranslation();
  const serviceConfig = prefillServiceConfig(serviceId);
  const serviceBasePath = serviceConfig.serviceBasePath;
  const ServiceIcon = serviceConfig.icon;
  const hasExpiredRef = useRef(false);
  const gamesCacheRef = useRef<{
    sessionId: string | null;
    fetchedAt: number;
    ownedGames: OwnedGame[];
    cachedAppIds: string[];
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

  const {
    isAdmin,
    steamPrefillEnabled,
    epicPrefillEnabled,
    battlenetPrefillEnabled,
    riotPrefillEnabled,
    xboxPrefillEnabled
  } = useAuth();

  // Main SignalR hub for system-level events (PrefillDefaultsChanged)
  const { on: onSignalR, off: offSignalR } = useSignalR();

  // Local UI state
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<string[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);
  const [cachedAppIds, setCachedAppIds] = useState<string[]>([]);
  const [isUsingGamesCache, setIsUsingGamesCache] = useState(false);

  // Prefill settings state
  const [selectedOS, setSelectedOS] = useState<string[]>(['windows', 'linux', 'macos']);
  const [maxConcurrency, setMaxConcurrency] = useState<string>('auto');
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
        const limit: number | null = data.maxThreadLimit ?? null;
        setMaxThreadLimit(limit);

        // Clamp concurrency to the guest thread limit so the dropdown
        // never selects a value that exceeds the allowed maximum
        let concurrency: string = data.maxConcurrency || 'auto';
        // Migrate legacy "max" saved value to numeric equivalent
        if (concurrency === 'max') {
          concurrency = String(limit ?? 256);
        }
        if (limit != null) {
          const numeric = parseInt(concurrency, 10);
          if (!isNaN(numeric) && numeric > limit) {
            concurrency = String(limit);
          }
        }
        setMaxConcurrency(concurrency);
      }
    } catch {
      // Failed to load defaults - will use existing values
    }
  }, []);

  // Load on mount
  useEffect(() => {
    loadPrefillDefaults();
  }, [loadPrefillDefaults]);

  // Listen for PrefillDefaultsChanged (admin changes OS/concurrency),
  // GuestPrefillConfigChanged / Epic / Xbox (admin changes system-wide guest thread limits),
  // and UserPreferencesUpdated (admin changes per-session thread limit) - re-fetch
  // to get session-resolved effective maxThreadLimit
  useEffect(() => {
    onSignalR('PrefillDefaultsChanged', loadPrefillDefaults);
    onSignalR('GuestPrefillConfigChanged', loadPrefillDefaults);
    onSignalR('EpicGuestPrefillConfigChanged', loadPrefillDefaults);
    onSignalR('XboxGuestPrefillConfigChanged', loadPrefillDefaults);
    onSignalR('UserPreferencesUpdated', loadPrefillDefaults);
    return () => {
      offSignalR('PrefillDefaultsChanged', loadPrefillDefaults);
      offSignalR('GuestPrefillConfigChanged', loadPrefillDefaults);
      offSignalR('EpicGuestPrefillConfigChanged', loadPrefillDefaults);
      offSignalR('XboxGuestPrefillConfigChanged', loadPrefillDefaults);
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
    } catch {
      // Failed to save defaults
    }
  }, []);

  // Wrapper setters that also persist to API
  const handleOSChange = useCallback(
    (newOS: string[]) => {
      setSelectedOS(newOS);
      savePrefillDefaults(newOS, undefined);
    },
    [savePrefillDefaults]
  );

  const handleConcurrencyChange = useCallback(
    (newConcurrency: string) => {
      setMaxConcurrency(newConcurrency);
      savePrefillDefaults(undefined, newConcurrency);
    },
    [savePrefillDefaults]
  );

  // Confirmation dialog state
  const [pendingConfirmCommand, setPendingConfirmCommand] = useState<CommandType | null>(null);
  const [estimatedSize, setEstimatedSize] = useState<{
    bytes: number;
    loading: boolean;
    error?: string;
    apps?: {
      appId: string;
      name: string;
      downloadSize: number;
      isUnsupportedOs?: boolean;
      unavailableReason?: string;
    }[];
    message?: string;
  }>({ bytes: 0, loading: false });

  // Handle auth state changes from backend SignalR events
  const handleAuthStateChanged = useCallback(
    (newState: DaemonAuthState) => {
      // Backend emits all nine DaemonAuthState values. The three directly-assigned values
      // (Authenticated/LoggingIn/NotAuthenticated) come from PrefillDaemonServiceBase.cs;
      // the sub-states (UsernameRequired/PasswordRequired/TwoFactorRequired/SteamGuardRequired/
      // DeviceConfirmationRequired/AuthorizationUrlRequired) are mapped from the external
      // daemon's socket protocol in PrefillDaemonServiceBase.Notifications.cs:20-25.
      switch (newState) {
        case 'Authenticated':
          signalR.setIsLoggedIn(true);
          setShowAuthModal(false);
          authActions.resetAuthForm();
          addLog('success', t('prefill.log.loginSuccess'));
          break;
        case 'UsernameRequired':
        case 'PasswordRequired':
          // Daemon needs the user to enter credentials - show the auth modal.
          // Both map to the same UI because the modal collects username + password together.
          authActions.resetAuthForm();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.credentialsRequired'));
          break;
        case 'TwoFactorRequired':
          trigger2FAPrompt();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.twoFactorRequired'));
          break;
        case 'SteamGuardRequired':
          // Steam Guard email code - daemon sent an email, user enters the code.
          triggerEmailPrompt();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.steamGuardRequired'));
          break;
        case 'DeviceConfirmationRequired':
          // User must approve the login attempt on their Steam Mobile App.
          authActions.setWaitingForMobileConfirmation(true);
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.deviceConfirmationRequired'));
          break;
        case 'AuthorizationUrlRequired':
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.authorizationUrlRequired'));
          break;
        case 'NotAuthenticated':
          signalR.setIsLoggedIn(false);
          break;
        case 'LoggingIn':
          // Transient state - daemon is performing the login handshake.
          // No UI action needed; the *Required states will follow if anything more is needed.
          break;
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [addLog, t]
  );

  // SignalR hook - manages connection, session, and progress
  const signalR = usePrefillSignalR({
    onSessionEnd,
    addLog,
    setBackgroundCompletion,
    clearBackgroundCompletion,
    isCompletionDismissed,
    onAuthStateChanged: handleAuthStateChanged,
    clearAllPrefillStorage,
    hubPath,
    serviceId
  });

  // Auth hook for container-based authentication (supports both Steam and Epic)
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
    onDeviceConfirmationTimeout: () => {
      setShowAuthModal(false);
    },
    serviceId
  });

  // Timer for session countdown
  useEffect(() => {
    if (!signalR.session || signalR.session.status !== 'Active') return;
    hasExpiredRef.current = false;

    const interval = setInterval(() => {
      // Ensure UTC interpretation for timestamps without timezone suffix
      const expiresAtStr = signalR.session!.expiresAt;
      const expiresAtUtc =
        expiresAtStr.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(expiresAtStr)
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
        // Expiry is already signalled downstream by `timeRemaining <= 0`
        // (every consumer guards with `status === 'Active' && timeRemaining > 0`).
        // Previously we also mutated session.status to a synthetic 'Expired' - nothing
        // read that value, so it was dead state. Dropped to keep the DTO status aligned
        // with the backend's `DaemonSessionStatus` union.
      }
    }, 1000);

    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    signalR.session,
    signalR.setSession,
    signalR.setIsLoggedIn,
    signalR.setTimeRemaining,
    signalR.setError,
    t
  ]);

  // Auto-create session when service was started from home page
  useEffect(() => {
    if (
      pendingService &&
      !signalR.isInitializing &&
      !signalR.isCreating &&
      !signalR.isConnecting &&
      !signalR.session
    ) {
      signalR.createSession(clearLogs);
      onPendingServiceHandled();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    pendingService,
    signalR.isInitializing,
    signalR.isCreating,
    signalR.isConnecting,
    signalR.session,
    signalR.createSession,
    clearLogs,
    onPendingServiceHandled
  ]);

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

      if (maxConcurrency !== 'auto') {
        const parsed = parseInt(maxConcurrency, 10);
        if (!isNaN(parsed) && parsed > 0) {
          requestBody.maxConcurrency = parsed;
        }
      }

      const response = await fetch(`${API_BASE}/${serviceBasePath}/sessions/${sessionId}/prefill`, {
        method: 'POST',
        credentials: 'include',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(requestBody)
      });

      if (!response.ok) {
        const error = (await response.json().catch(() => ({}))) as {
          error?: string;
          message?: string;
        };
        // The 409 "already running" body shape is `{ error: ... }`; other error paths use
        // `{ message: ... }`. Read both so the specific reason surfaces regardless of shape.
        throw new Error(
          error.error ??
            error.message ??
            t('prefill.errors.httpStatus', { status: response.status })
        );
      }

      return response.json();
    },
    [selectedOS, maxConcurrency, signalR.isCancelling, serviceBasePath, t]
  );

  const loadGames = useCallback(
    async (force = false) => {
      if (!signalR.session) return;

      setIsLoadingGames(true);
      try {
        const gamesCache = gamesCacheRef.current;
        const isCacheFresh =
          !force &&
          gamesCache &&
          gamesCache.hasData &&
          gamesCache.sessionId === signalR.session.id &&
          Date.now() - gamesCache.fetchedAt < gamesCacheWindowMs;

        if (isCacheFresh) {
          setOwnedGames(gamesCache.ownedGames);
          setCachedAppIds(gamesCache.cachedAppIds);
          setIsUsingGamesCache(true);
          return;
        }

        setIsUsingGamesCache(false);

        // Fetch owned games via direct API call
        const gamesResponse = await fetch(
          `${API_BASE}/${serviceBasePath}/sessions/${signalR.session.id}/games`,
          { credentials: 'include' }
        );
        if (!gamesResponse.ok) {
          throw new Error(`Failed to get games: HTTP ${gamesResponse.status}`);
        }
        const games = await gamesResponse.json();
        const normalizedGames = (games || []).map((game: OwnedGame) => ({
          ...game,
          appId: String(game.appId)
        }));
        setOwnedGames(normalizedGames);
        if (serviceId !== 'battlenet' && serviceId !== 'riot') {
          // Battle.net/Riot have a fixed public catalog - "owned games" framing is inaccurate
          addLog('info', t('prefill.log.foundGames', { count: normalizedGames.length }));
        }

        // Get cached apps via ApiService and verify against daemon manifests/build versions
        const cachedApps = await ApiService.getPrefillCachedApps();
        let cachedIds = cachedApps.map((a) => String(a.appId));

        if (cachedIds.length > 0) {
          try {
            const cacheStatus = await ApiService.getPrefillCacheStatus(
              signalR.session.id,
              cachedIds,
              serviceBasePath
            );
            cachedIds = cacheStatus?.upToDateAppIds?.length
              ? cacheStatus.upToDateAppIds.map((id: string) => String(id))
              : [];
          } catch {
            cachedIds = [];
          }
        }

        setCachedAppIds(cachedIds);
        gamesCacheRef.current = {
          sessionId: signalR.session.id,
          fetchedAt: Date.now(),
          ownedGames: normalizedGames,
          cachedAppIds: cachedIds,
          hasData: true
        };
        if (cachedIds.length > 0) {
          addLog('info', t('prefill.log.gamesCached', { count: cachedIds.length }));
        }
      } catch {
        addLog('error', t('prefill.log.failedLoadLibrary'));
      } finally {
        setIsLoadingGames(false);
      }
    },
    [signalR.session, addLog, t, serviceBasePath, gamesCacheWindowMs, serviceId]
  );

  const executeCommand = useCallback(
    async (commandType: CommandType) => {
      if (!signalR.session || !signalR.hubConnection.current) return;
      if (signalR.session.status !== 'Active' || signalR.timeRemaining <= 0) {
        signalR.setError(t('prefill.errors.sessionExpired'));
        addLog('warning', t('prefill.errors.sessionExpired'));
        return;
      }

      // Start guard: never POST a second prefill while one is already running on the daemon.
      // `isPrefillActive` is now reliable (re-hydrated from server `isPrefilling`), so this also
      // covers the previously-broken "already running but no bar" state. Surface "already
      // running" instead of spawning a duplicate daemon run.
      const isPrefillCommand = commandType.startsWith('prefill');
      if (isPrefillCommand && signalR.isPrefillActive) {
        addLog('warning', t('prefill.log.alreadyRunning'));
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
            addLog(
              'download',
              t('prefill.log.startingPrefillSelected', { count: selectedAppIds.length })
            );
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
            if (serviceId !== 'battlenet' && serviceId !== 'riot') {
              // Battle.net/Riot have a fixed public catalog - no "library" to load
              addLog('progress', t('prefill.log.loadingGameLibrary'));
            }
            setShowGameSelection(true);
            await loadGames();
            break;
          }
          case 'clear-temp': {
            addLog('info', t('prefill.log.clearingTempCache'));
            try {
              await signalR.hubConnection.current.invoke('ClearCacheAsync', signalR.session.id);
              addLog('success', t('prefill.log.tempCacheCleared'));
            } catch (err) {
              addLog('error', getErrorMessage(err) || t('prefill.log.failedClearCache'));
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
              addLog('error', getErrorMessage(err) || t('prefill.log.failedClearCacheDb'));
            }
            break;
          }
        }
      } catch (err) {
        addLog('error', getErrorMessage(err) || t('prefill.log.commandFailed'));
        // V1: roll back the OPTIMISTIC 'starting' bar painted in handleConfirmCommand. If the
        // prefill POST threw (409 already-running / network / non-ok) no daemon run started, so no
        // terminal event will ever arrive to clear it — without this the fake "Contacting daemon..."
        // bar (and its live-but-dead Cancel) sticks and the start-guard blocks all retries. Gated to
        // the prefill start path so a genuinely-running prefill's bar (which the start-guard already
        // protects from re-entry) is never clobbered.
        if (isPrefillCommand) {
          signalR.setIsPrefillActive(false);
          signalR.setPrefillProgress(null);
          try {
            sessionStorage.removeItem(STORAGE_KEYS.PREFILL_IN_PROGRESS);
          } catch {
            /* ignore */
          }
        }
      } finally {
        setIsExecuting(false);
      }
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [
      signalR.session,
      signalR.hubConnection,
      signalR.expectedAppCountRef,
      signalR.timeRemaining,
      signalR.isPrefillActive,
      signalR.setError,
      callPrefillApi,
      selectedAppIds,
      addLog,
      loadGames,
      t
    ]
  );

  const handleEndSession = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    try {
      await signalR.hubConnection.current.invoke('EndSessionAsync', signalR.session.id);
    } catch {
      // Session end failed - will be cleaned up by timeout
    }
  }, [signalR.session, signalR.hubConnection]);

  const handleCancelLogin = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    try {
      await signalR.hubConnection.current.invoke('CancelLoginAsync', signalR.session.id);
      setShowAuthModal(false);
      authActions.resetAuthForm();
    } catch {
      // Cancel login failed
    }
  }, [signalR.session, signalR.hubConnection, authActions]);

  const handleCancelPrefill = useCallback(() => {
    // Full cancel orchestration (hard-stop animations + reactive "Cancelling..." state + watchdog
    // + hub invoke) lives in the SignalR hook so it can reach the internal animation refs.
    void signalR.cancelPrefill();
  }, [signalR]);

  const handleOpenAuthModal = useCallback(() => {
    authActions.resetAuthForm();
    setShowAuthModal(true);
  }, [authActions]);

  const handleSaveGameSelection = useCallback(
    async (appIds: string[]) => {
      if (!signalR.session) return;
      const normalizedAppIds = appIds.map((id) => String(id));

      try {
        const response = await fetch(
          `${API_BASE}/${serviceBasePath}/sessions/${signalR.session.id}/selected-apps`,
          {
            method: 'POST',
            credentials: 'include',
            headers: {
              'Content-Type': 'application/json'
            },
            body: JSON.stringify({ appIds: normalizedAppIds })
          }
        );
        if (!response.ok) {
          throw new Error(`Failed to save selection: HTTP ${response.status}`);
        }
        setSelectedAppIds(normalizedAppIds);
        setShowGameSelection(false);
        addLog('success', t('prefill.log.selectedGames', { count: normalizedAppIds.length }));
      } catch {
        addLog('error', t('prefill.log.failedSaveSelection'));
      }
    },
    [signalR.session, addLog, serviceBasePath, t]
  );

  // Confirmation dialog logic
  const fetchEstimatedSize = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current || selectedAppIds.length === 0) return;

    setEstimatedSize({ bytes: 0, loading: true });

    try {
      const status = (await signalR.hubConnection.current.invoke(
        'GetSelectedAppsStatusAsync',
        signalR.session.id,
        selectedOS
      )) as {
        totalDownloadSize: number;
        message?: string;
        apps?: {
          appId: string;
          name: string;
          downloadSize: number;
          isUnsupportedOs?: boolean;
          unavailableReason?: string;
        }[];
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
      // Background size estimate - already has its own inline error slot (estimatedSize.error) that
      // the UI reads directly, so no notification is needed; log the detail for diagnosis.
      console.error('[PrefillPanel] Failed to estimate size:', getErrorMessage(err));
      setEstimatedSize({
        bytes: 0,
        loading: false,
        error: t('prefill.errors.unableEstimateSize')
      });
    }
  }, [signalR.session, signalR.hubConnection, selectedAppIds, selectedOS, t]);

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
    if (!pendingConfirmCommand) return;

    // Continue start-guard: short-circuit if a prefill is already running (reliable now that
    // isPrefillActive is re-hydrated from server truth) so Continue can't spawn a duplicate run.
    if (pendingConfirmCommand.startsWith('prefill') && signalR.isPrefillActive) {
      addLog('warning', t('prefill.log.alreadyRunning'));
      setPendingConfirmCommand(null);
      setEstimatedSize({ bytes: 0, loading: false });
      return;
    }

    // Optimistic start: paint a 'starting' bar immediately so there is no dead gap between
    // Continue and the first server PrefillProgress/PrefillStateChanged event.
    if (pendingConfirmCommand.startsWith('prefill')) {
      signalR.isCancelling.current = false;
      signalR.setIsPrefillActive(true);
      signalR.setPrefillProgress({
        state: 'starting',
        message: t('prefill.progress.startingMessage'),
        currentAppId: '',
        currentAppName: undefined,
        percentComplete: 0,
        bytesDownloaded: 0,
        totalBytes: 0,
        bytesPerSecond: 0,
        elapsedSeconds: 0
      });
    }

    executeCommand(pendingConfirmCommand);
    setPendingConfirmCommand(null);
    setEstimatedSize({ bytes: 0, loading: false });
  }, [pendingConfirmCommand, executeCommand, signalR, addLog, t]);

  const handleCancelConfirm = useCallback(() => {
    setPendingConfirmCommand(null);
    setEstimatedSize({ bytes: 0, loading: false });
  }, []);

  const isLoadingSession = signalR.isInitializing || signalR.isCreating;
  const isSessionActive =
    !!signalR.session && signalR.session.status === 'Active' && signalR.timeRemaining > 0;
  const isSessionExpired = !!signalR.session && !isSessionActive;

  // Battle.net and Riot prefill are fully anonymous - no account login ever. Treat the client
  // as always "logged in"/ready so the auth login card stays hidden, the "Login Required"
  // notice never shows, and prefill commands are enabled the moment a session is active.
  const isAnonymousService = serviceId === 'battlenet' || serviceId === 'riot';
  const isReadyForCommands = isAnonymousService || signalR.isLoggedIn;

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

  // No session, not loading, not pending - show home page
  if (!signalR.session && !isLoadingSession && !pendingService) {
    return (
      <>
        {/* Battle.net is anonymous - no auth modal */}
        {serviceId === 'battlenet' || serviceId === 'riot' ? null : serviceId === 'epic' ? (
          <EpicAuthModal
            opened={showAuthModal}
            onClose={() => setShowAuthModal(false)}
            state={authState}
            actions={authActions}
            onCancelLogin={handleCancelLogin}
          />
        ) : serviceId === 'xbox' ? (
          <XboxAuthModal
            opened={showAuthModal}
            onClose={() => setShowAuthModal(false)}
            state={authState}
            actions={authActions}
            onCancelLogin={handleCancelLogin}
          />
        ) : (
          <SteamAuthModal
            opened={showAuthModal}
            onClose={() => setShowAuthModal(false)}
            state={authState}
            actions={authActions}
            isPrefillMode={true}
            onCancelLogin={handleCancelLogin}
          />
        )}
        <PrefillHomePage
          onServiceStart={onServiceStart}
          error={signalR.error}
          errorService={serviceId as GameServiceId}
          isAdmin={isAdmin}
          steamPrefillEnabled={steamPrefillEnabled}
          epicPrefillEnabled={epicPrefillEnabled}
          battlenetPrefillEnabled={battlenetPrefillEnabled}
          riotPrefillEnabled={riotPrefillEnabled}
          xboxPrefillEnabled={xboxPrefillEnabled}
        />
      </>
    );
  }

  // Loading/Creating/Pending state
  if (!signalR.session) {
    const status = signalR.isCreating ? 'creating' : 'checking';
    return <PrefillLoadingState status={status} serviceId={serviceId} />;
  }

  // Active session - full interface
  return (
    <div className="space-y-4 animate-fade-in">
      {/* Auth Modal - Battle.net and Riot are anonymous, so no modal */}
      {serviceId === 'battlenet' || serviceId === 'riot' ? null : serviceId === 'epic' ? (
        <EpicAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          onCancelLogin={handleCancelLogin}
        />
      ) : serviceId === 'xbox' ? (
        <XboxAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          onCancelLogin={handleCancelLogin}
        />
      ) : (
        <SteamAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          isPrefillMode={true}
          onCancelLogin={handleCancelLogin}
        />
      )}

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
        <div className="p-4 rounded-lg flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 bg-[var(--theme-warning-subtle)] border border-[var(--theme-warning-strong)]">
          <div className="flex items-start gap-3">
            <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-warning)]" />
            <div>
              <p className="font-medium text-sm text-[var(--theme-warning-text)]">
                {t('prefill.sessionExpired.title')}
              </p>
              <p className="text-sm text-themed-muted">{t('prefill.sessionExpired.message')}</p>
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
          <div
            className={`w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0 ${serviceConfig.iconBgClass}`}
          >
            <ServiceIcon size={24} className="text-white" />
          </div>
          <div>
            <h1 className="text-xl font-bold text-themed-primary">
              {serviceId === 'epic'
                ? t('prefill.titleEpic')
                : serviceId === 'battlenet'
                  ? t('prefill.titleBattlenet')
                  : serviceId === 'riot'
                    ? t('prefill.titleRiot')
                    : serviceId === 'xbox'
                      ? t('prefill.titleXbox')
                      : t('prefill.title')}
            </h1>
            <p className="text-sm text-themed-muted">{t('prefill.subtitle')}</p>
          </div>
        </div>

        <div className="flex items-center gap-3 w-full sm:w-auto">
          {/* Session Timer — non-interactive chip, h-10 matches End Session size="md" (~40px) */}
          <div
            className={`inline-flex items-center gap-2 h-10 px-4 rounded-lg flex-1 sm:flex-initial justify-center border ${
              signalR.timeRemaining < 600
                ? 'bg-[var(--theme-warning-subtle)] border-[var(--theme-warning-strong)]'
                : 'bg-[var(--theme-bg-tertiary)] border-[var(--theme-border-secondary)]'
            }`}
          >
            <Timer
              className={`h-4 w-4 ${
                signalR.timeRemaining < 600
                  ? 'text-[var(--theme-warning)]'
                  : 'text-[var(--theme-text-muted)]'
              }`}
            />
            <span
              className={`font-mono font-semibold tabular-nums ${
                signalR.timeRemaining < 600
                  ? 'text-[var(--theme-warning-text)]'
                  : 'text-[var(--theme-text-primary)]'
              }`}
            >
              {formatTimeRemaining(signalR.timeRemaining)}
            </span>
          </div>

          {/* End Session Button */}
          {!isSessionExpired && (
            <Button
              variant="filled"
              color="red"
              size="md"
              onClick={handleEndSession}
              // min-h-10 holds this at the Session Timer chip's fixed h-10 even when the label
              // collapses to icon-only below sm - without it, icon+padding alone falls short.
              className="flex-shrink-0 min-h-10"
            >
              <X className="h-4 w-4" />
              <span className="hidden sm:inline">{t('prefill.endSession')}</span>
            </Button>
          )}
        </div>
      </div>

      {/* Error Banner */}
      {signalR.error && (
        <div className="p-4 rounded-lg flex items-center gap-3 bg-[var(--theme-error-bg)] border border-[var(--theme-error-strong)]">
          <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-error)]" />
          <span className="text-[var(--theme-error-text)]">{signalR.error}</span>
        </div>
      )}

      {/* Main Content - Two Column Layout */}
      <div className="grid grid-cols-1 xl:grid-cols-3 gap-4">
        {/* Left Column - Controls */}
        <div className="xl:col-span-2 space-y-4">
          {/* Authentication Card - Battle.net and Riot are anonymous (no login), so this is hidden */}
          {serviceId !== 'battlenet' && serviceId !== 'riot' && (
            <Card padding="md">
              <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                <div className="flex items-center gap-3">
                  <div
                    className={`w-10 h-10 rounded-lg flex items-center justify-center ${
                      signalR.isLoggedIn
                        ? 'bg-[var(--theme-success-subtle)]'
                        : 'bg-[var(--theme-warning-subtle)]'
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
                    size="md"
                    onClick={handleOpenAuthModal}
                    className="flex-shrink-0 w-full sm:w-auto"
                  >
                    {serviceId === 'epic' ? (
                      <EpicIcon size={18} className="text-[var(--theme-button-text)]" />
                    ) : serviceId === 'xbox' ? (
                      <XboxIcon size={18} className="text-[var(--theme-button-text)]" />
                    ) : (
                      <SteamIcon size={18} />
                    )}
                    {serviceId === 'epic'
                      ? t('prefill.auth.loginToEpic', 'Login to Epic')
                      : serviceId === 'xbox'
                        ? t('prefill.auth.loginToXbox', 'Login to Xbox')
                        : t('prefill.auth.loginToSteam')}
                  </Button>
                )}
              </div>
            </Card>
          )}

          {/* Network Status Card */}
          <NetworkStatusSection diagnostics={signalR.session?.networkDiagnostics} />

          {/* Background Completion Notification Banner */}
          {backgroundCompletion && !signalR.prefillProgress && (
            <CompletionBanner
              completion={backgroundCompletion}
              onDismiss={clearBackgroundCompletion}
            />
          )}

          {/* Download Progress Card */}
          {signalR.prefillProgress && isSessionActive && (
            <PrefillProgressCard
              progress={signalR.prefillProgress}
              onCancel={handleCancelPrefill}
              isCancelling={signalR.isCancellingState}
            />
          )}

          {/* Command Buttons */}
          <PrefillCommandButtons
            isLoggedIn={isReadyForCommands}
            isExecuting={isExecuting}
            isPrefillActive={signalR.isPrefillActive}
            isSessionActive={isSessionActive}
            isUserAuthenticated={isAdmin}
            selectedAppIds={selectedAppIds}
            selectedOS={selectedOS}
            maxConcurrency={maxConcurrency}
            maxThreadLimit={maxThreadLimit}
            supportedCommands={serviceConfig.prefillCommands}
            supportedOperatingSystems={serviceConfig.supportedOperatingSystems}
            onCommandClick={handleCommandClick}
            onSelectedOSChange={handleOSChange}
            onMaxConcurrencyChange={handleConcurrencyChange}
          />
        </div>

        {/* Right Column - Activity Log */}
        <div className="xl:col-span-1">
          <Card padding="none" className="overflow-hidden">
            <div className="px-4 pt-4 pb-3 flex items-center gap-3 border-b border-[var(--theme-border-primary)]">
              <div className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-[var(--theme-accent-subtle)]">
                <ScrollText className="h-4 w-4 text-[var(--theme-accent)]" />
              </div>
              <div>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('prefill.activityLog.title')}
                </h3>
                <p className="text-xs text-themed-muted">{t('prefill.activityLog.subtitle')}</p>
              </div>
            </div>
            <CardContent className="p-0">
              <ActivityLog
                entries={logEntries}
                className="border-0 rounded-none"
                serviceId={serviceId}
              />
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
