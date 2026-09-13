import { useEffect, useCallback, useState, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { resolveCachedAppIds } from './cachedApps';
import { Card, CardContent } from '../../ui/Card';
import { Button } from '../../ui/Button';
import { Tooltip } from '../../ui/Tooltip';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { SteamAuthModal } from '@components/modals/auth/SteamAuthModal';
import { EpicAuthModal } from '@components/modals/auth/EpicAuthModal';
import { XboxAuthModal } from '@components/modals/auth/XboxAuthModal';
import { usePrefillSteamAuth } from '@hooks/usePrefillSteamAuth';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { ActivityLog } from './ActivityLog';
import { GameSelectionModal, type OwnedGame } from './GameSelectionModal';
import { NetworkStatusSection } from './NetworkStatusSection';
import ApiService from '@services/api.service';
import { ApiError, assertOk } from '@services/apiError';
import { usePrefillContext } from '@contexts/usePrefillContext';
import { useAuth } from '@contexts/useAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { API_BASE, STORAGE_KEYS } from '@utils/constants';
import { sessionStore } from '@utils/storage';
import { getErrorMessage } from '@utils/error';
import { parseUtcDate } from '@utils/timezone';

import { ScrollText, Timer, LogIn, AlertCircle } from 'lucide-react';

import { useGameService } from '@contexts/useGameService';
import type { GameServiceId } from '@/types/gameService';

// Import extracted components
import { PrefillHomePage } from './PrefillHomePage';
import { PrefillLoadingState } from './PrefillLoadingState';
import { PrefillProgressCard } from './PrefillProgressCard';
import {
  getPrefillRunProgress,
  isPrefillRunActive,
  supportsConcurrentPrefill
} from './hooks/prefillTypes';
import { PrefillCommandButtons } from './PrefillCommandButtons';
import { CompletionBanner } from './CompletionBanner';
import { usePrefillSignalR } from './hooks/usePrefillSignalR';
import { prefillServiceConfig } from './hooks/prefillServiceConfig';
import {
  type PrefillPanelProps,
  type CommandType,
  type EstimatedSize,
  type EstimatedSizeApp,
  formatBytes,
  formatTimeRemaining
} from './types';
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
  const serviceName = t(serviceConfig.serviceNameKey);
  const hasExpiredRef = useRef(false);
  const gamesCacheRef = useRef<{
    sessionId: string | null;
    fetchedAt: number;
    ownedGames: OwnedGame[];
    cachedAppIds: string[];
    hasData: boolean;
  } | null>(null);
  const gamesCacheWindowMs = 5 * 60 * 1000;
  const reloadGamesRef = useRef<Promise<void> | null>(null);
  const reloadGamesAgainRef = useRef(false);
  const gamesRequestRef = useRef<AbortController | null>(null);
  const gamesEpochRef = useRef(0);
  const gamesKeyRef = useRef('');
  const gameAuthRef = useRef<{ key: string; authenticated: boolean } | null>(null);

  // Use context for log entries (persists across tab switches)
  const {
    logEntries,
    addLog,
    clearLogs,
    backgroundCompletion,
    runCompletions,
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

  // Main SignalR hub for system-level events (PrefillDefaultsChanged). Named apart from
  // `signalR.isConnected` below, which is the per-service prefill daemon hub and carries none of
  // these broadcasts.
  const { on: onSignalR, off: offSignalR, isConnected: isMainHubConnected } = useSignalR();
  const { notifyError } = useErrorHandler();

  // Local UI state
  const [showAuthModal, setShowAuthModal] = useState(false);
  const [isExecuting, setIsExecuting] = useState(false);

  // Game selection state
  const [ownedGames, setOwnedGames] = useState<OwnedGame[]>([]);
  const [selectedAppIds, setSelectedAppIds] = useState<string[]>([]);
  const [showGameSelection, setShowGameSelection] = useState(false);
  const [isLoadingGames, setIsLoadingGames] = useState(false);
  const [cachedAppIds, setCachedAppIds] = useState<string[]>([]);
  const [unknownAppIds, setUnknownAppIds] = useState<string[]>([]);
  const [gameLoadError, setGameLoadError] = useState<string | null>(null);
  const [isUsingGamesCache, setIsUsingGamesCache] = useState(false);
  const [removingAppId, setRemovingAppId] = useState<string | null>(null);
  const [isClearingAllCache, setIsClearingAllCache] = useState(false);

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

      await fetch(
        `${API_BASE}/system/prefill-defaults`,
        ApiService.getJsonFetchOptions(body, { method: 'PATCH' })
      );
    } catch {
      // Failed to save defaults
    }
  }, []);

  // Wrapper setters that also persist to API
  const handleOSChange = useCallback(
    (newOS: string[]) => {
      setSelectedOS(newOS);
      savePrefillDefaults(newOS, undefined);
      addLog(
        'info',
        t('prefill.log.platformsChanged', {
          platforms: newOS.map((v) => t(`prefill.settings.os.${v}.label`)).join(', ')
        })
      );
    },
    [savePrefillDefaults, addLog, t]
  );

  const handleConcurrencyChange = useCallback(
    (newConcurrency: string) => {
      setMaxConcurrency(newConcurrency);
      savePrefillDefaults(undefined, newConcurrency);
      addLog('info', t('prefill.log.connectionsChanged', { value: newConcurrency }));
    },
    [savePrefillDefaults, addLog, t]
  );

  // Confirmation dialog state
  const [pendingConfirmCommand, setPendingConfirmCommand] = useState<CommandType | null>(null);
  const [estimatedSize, setEstimatedSize] = useState<EstimatedSize>({
    bytes: 0,
    loading: false
  });
  // Monotonic id for size-estimate requests. Each fetch captures the current id; a slower
  // in-flight request whose id is no longer current (the selection changed, or a newer retry
  // started) must not commit its result over the newer state.
  const estimateRequestIdRef = useRef(0);
  // Selection+OS+bytes of the last estimate written to the activity log, so refetches of the
  // same answer (reconnects, resolve polling) do not repeat the entry.
  const estimateLoggedRef = useRef<string | null>(null);

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
          addLog('success', t('prefill.log.loginSuccess', { service: serviceName }));
          break;
        case 'UsernameRequired':
        case 'PasswordRequired':
          // Daemon needs the user to enter credentials - show the auth modal.
          // Both map to the same UI because the modal collects username + password together.
          authActions.resetAuthForm();
          setShowAuthModal(true);
          addLog('auth', t('prefill.log.credentialsRequired', { service: serviceName }));
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
    [addLog, t, serviceName]
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
    loginDeadline: authLoginDeadline,
    trigger2FAPrompt,
    triggerEmailPrompt
  } = usePrefillSteamAuth({
    sessionId: signalR.session?.id ?? null,
    hubConnection: signalR.hubConnection.current,
    onSuccess: () => setShowAuthModal(false),
    onError: () => {
      /* Keep modal open on error */
    },
    serviceId
  });

  // Timer for session countdown
  useEffect(() => {
    if (!signalR.session || signalR.session.status !== 'Active') return;
    hasExpiredRef.current = false;

    const interval = setInterval(() => {
      const remaining = Math.max(
        0,
        Math.floor((parseUtcDate(signalR.session!.expiresAt).getTime() - Date.now()) / 1000)
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
      if (
        supportsConcurrentPrefill(signalR.session) &&
        !options.all &&
        !options.recent &&
        !options.recentlyPurchased &&
        !options.top
      ) {
        requestBody.appIds = [...selectedAppIds];
      }

      if (selectedOS.length > 0 && selectedOS.length < 3) {
        requestBody.operatingSystems = selectedOS;
      }

      if (maxConcurrency !== 'auto') {
        const parsed = parseInt(maxConcurrency, 10);
        if (!isNaN(parsed) && parsed > 0) {
          requestBody.maxConcurrency = parsed;
        }
      }

      const response = await fetch(
        `${API_BASE}/${serviceBasePath}/sessions/${sessionId}/prefill`,
        ApiService.getJsonFetchOptions(requestBody, { method: 'POST' })
      );

      // The 409 "already running" body shape is `{ error: ... }`; other error paths use
      // `{ message: ... }`. assertOk reads both, so the specific reason surfaces either way.
      await assertOk(response);

      const result = await response.json();
      await signalR.refreshRuns();
      return result;
    },
    [selectedOS, maxConcurrency, signalR, selectedAppIds, serviceBasePath]
  );

  useEffect(() => {
    gamesRequestRef.current?.abort();
    gamesKeyRef.current = `${serviceId}:${signalR.session?.id ?? ''}`;
    gamesEpochRef.current += 1;
    reloadGamesRef.current = null;
    reloadGamesAgainRef.current = false;
    gamesCacheRef.current = null;
    setCachedAppIds([]);
    setUnknownAppIds([]);
    setGameLoadError(null);
    setOwnedGames([]);
    setShowGameSelection(false);
    setIsLoadingGames(false);
    return () => {
      gamesRequestRef.current?.abort();
      gamesEpochRef.current += 1;
      gamesKeyRef.current = '';
      reloadGamesRef.current = null;
      reloadGamesAgainRef.current = false;
    };
  }, [serviceId, signalR.session?.id]);

  gamesKeyRef.current = `${serviceId}:${signalR.session?.id ?? ''}`;

  const loadGames = useCallback(
    async (force = false) => {
      if (!signalR.session) return;
      const key = `${serviceId}:${signalR.session.id}`;
      if (gamesKeyRef.current !== key) return;
      gamesRequestRef.current?.abort();
      const controller = new AbortController();
      gamesRequestRef.current = controller;
      const signal = AbortSignal.any([controller.signal, AbortSignal.timeout(45000)]);
      const isCurrent = () =>
        gamesRequestRef.current === controller &&
        !controller.signal.aborted &&
        gamesKeyRef.current === key;
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
          setUnknownAppIds([]);
          setIsUsingGamesCache(true);
          return;
        }

        setIsUsingGamesCache(false);
        setUnknownAppIds(ownedGames.map((game) => game.appId));

        // Fetch owned games via direct API call
        const gamesResponse = await fetch(
          `${API_BASE}/${serviceBasePath}/sessions/${signalR.session.id}/games`,
          { credentials: 'include', signal }
        );
        await assertOk(gamesResponse);
        const games: OwnedGame[] = await gamesResponse.json();
        const normalizedGames = (games || []).map((game: OwnedGame) => ({
          ...game,
          appId: String(game.appId)
        }));
        if (!isCurrent()) return;
        setOwnedGames(normalizedGames);
        if (serviceId !== 'battlenet' && serviceId !== 'riot') {
          // Battle.net/Riot have a fixed public catalog - "owned games" framing is inaccurate
          addLog('info', t('prefill.log.foundGames', { count: normalizedGames.length }));
        }

        // Get cached apps via ApiService and verify against daemon manifests/build versions
        const cachedApps = await ApiService.getPrefillCachedApps(serviceId, signal);
        const eligible = cachedApps
          .map((a) => String(a.appId))
          .filter((id) => normalizedGames.some((game) => game.appId === id));
        let cachedIds: string[] = [];
        let unknownIds = eligible;
        // Tracks whether the cached-status verification produced an authoritative answer. A
        // transient failure here must not be persisted as "nothing is cached" for the whole
        // cache window (that showed wrong cached badges until expiry) - mark the snapshot
        // non-authoritative instead so the next load re-verifies.
        let cacheStatusResolved = true;

        if (eligible.length > 0) {
          try {
            const cacheStatus = await ApiService.getPrefillCacheStatus(
              signalR.session.id,
              eligible,
              serviceBasePath,
              signal
            );
            cachedIds = cacheStatus.upToDateAppIds.filter((id) => eligible.includes(id));
            unknownIds = cacheStatus.unknownAppIds.filter((id) => eligible.includes(id));
            cacheStatusResolved = unknownIds.length === 0;
          } catch (error: unknown) {
            if (!isCurrent()) return;
            const stageKey = error instanceof ApiError ? error.body?.stageKey : null;
            setGameLoadError(
              stageKey === 'errors.steam.signInLost'
                ? t('errors.steam.signInLost')
                : stageKey === 'errors.steam.gameDetailsUnavailable'
                  ? t('errors.steam.gameDetailsUnavailable')
                  : t('errors.prefill.requestFailed')
            );
            cachedIds = [];
            cacheStatusResolved = false;
          }
        }

        // Same reason the snapshot is withheld: a failed check is not proof nothing is cached, so
        // the badges already on screen stay put rather than blanking mid-run.
        if (!isCurrent()) return;
        setCachedAppIds((previous) => resolveCachedAppIds(previous, cachedIds, unknownIds));
        setUnknownAppIds(unknownIds);
        if (cacheStatusResolved) setGameLoadError(null);
        // Only persist an authoritative snapshot: the cached-status check resolved AND the
        // library actually came back. An empty/failed transient is left uncached (hasData:false)
        // so a later load retries instead of reusing a wrong-empty library until cache expiry.
        gamesCacheRef.current = {
          sessionId: signalR.session.id,
          fetchedAt: Date.now(),
          ownedGames: normalizedGames,
          cachedAppIds: cachedIds,
          hasData: cacheStatusResolved && normalizedGames.length > 0
        };
        if (cachedIds.length > 0) {
          addLog('info', t('prefill.log.gamesCached', { count: cachedIds.length }));
        }
      } catch (error: unknown) {
        if (!isCurrent()) return;
        gamesCacheRef.current = null;
        setUnknownAppIds(ownedGames.map((game) => game.appId));
        const stageKey = error instanceof ApiError ? error.body?.stageKey : null;
        setGameLoadError(
          stageKey === 'errors.steam.signInLost'
            ? t('errors.steam.signInLost')
            : stageKey === 'errors.steam.gameDetailsUnavailable'
              ? t('errors.steam.gameDetailsUnavailable')
              : t('errors.prefill.requestFailed')
        );
        addLog('error', t('prefill.log.failedLoadLibrary'));
      } finally {
        if (isCurrent()) setIsLoadingGames(false);
      }
    },
    [signalR.session, addLog, t, serviceBasePath, gamesCacheWindowMs, serviceId, ownedGames]
  );
  const loadGamesRef = useRef(loadGames);
  loadGamesRef.current = loadGames;

  // Every source of a library reload goes through here: the PrefillCacheChanged broadcast, the
  // reconnect catch-up, and the clear/remove handlers that re-read on their own so the badges are
  // right with the socket down. One click reaches two of them, and a first-ever run emits one
  // event per newly cached game, so without this each reload's three sequential requests (the
  // daemon round trip among them) would stack. A caller arriving mid-pass is queued rather than
  // dropped - the running pass may have started before that caller's delete committed, since a
  // game finishing anywhere raises the same event - and awaits the pass that will include it.
  const reloadGamesOnce = useCallback((): Promise<void> => {
    if (reloadGamesRef.current) {
      reloadGamesAgainRef.current = true;
      return reloadGamesRef.current;
    }
    const epoch = gamesEpochRef.current;
    const key = gamesKeyRef.current;
    const pass = (async () => {
      try {
        do {
          reloadGamesAgainRef.current = false;
          await loadGamesRef.current(true);
        } while (
          reloadGamesAgainRef.current &&
          gamesEpochRef.current === epoch &&
          gamesKeyRef.current === key
        );
      } finally {
        if (gamesEpochRef.current === epoch && gamesKeyRef.current === key)
          reloadGamesRef.current = null;
      }
    })();
    reloadGamesRef.current = pass;
    return pass;
  }, []);

  useEffect(() => {
    const key = `${serviceId}:${signalR.session?.id ?? ''}`;
    const previous = gameAuthRef.current;
    gameAuthRef.current = { key, authenticated: signalR.isLoggedIn };
    if (previous?.key !== key || previous.authenticated === signalR.isLoggedIn) return;
    gamesCacheRef.current = null;
    if (!signalR.isLoggedIn) {
      gamesRequestRef.current?.abort();
      setIsLoadingGames(false);
      setUnknownAppIds(ownedGames.map((game) => game.appId));
    } else if (showGameSelection) {
      void reloadGamesOnce();
    }
  }, [
    serviceId,
    signalR.session?.id,
    signalR.isLoggedIn,
    showGameSelection,
    ownedGames,
    reloadGamesOnce
  ]);

  useEffect(() => {
    if (showGameSelection) return;
    gamesRequestRef.current?.abort();
    gamesEpochRef.current += 1;
    reloadGamesRef.current = null;
    reloadGamesAgainRef.current = false;
    setIsLoadingGames(false);
  }, [showGameSelection]);

  // The cached-depot table is shared by every container and every browser, so a game finishing
  // anywhere - or a cached entry removed anywhere - must reach this library without a Rescan.
  // The backend broadcasts this only when rows actually changed, so an all-already-cached run
  // cannot turn into one event per game.
  useEffect(() => {
    const handlePrefillCacheChanged = () => {
      void reloadGamesOnce();
    };
    onSignalR('PrefillCacheChanged', handlePrefillCacheChanged);
    return () => {
      offSignalR('PrefillCacheChanged', handlePrefillCacheChanged);
    };
  }, [onSignalR, offSignalR, reloadGamesOnce]);

  // A broadcast that lands while the socket is down is lost, leaving the cached badges stale until
  // the next Rescan. Re-read once the main hub is live again.
  useReconnectRefetch(isMainHubConnected, () => void reloadGamesOnce());

  const handleClearAllFromCache = useCallback(async () => {
    const epoch = gamesEpochRef.current;
    const key = gamesKeyRef.current;
    setIsClearingAllCache(true);
    try {
      await ApiService.clearAllPrefillCache(serviceId);
      if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
      gamesRequestRef.current?.abort();
      gamesCacheRef.current = null;
      setCachedAppIds([]);
      addLog('info', t('prefill.log.clearedAllFromCache'));
      // Same reasoning as the per-game removal below: re-read rather than waiting for the
      // broadcast, so the badges are right even with the socket down. The forced reload is also
      // what rewrites gamesCacheRef - blanking only the React state leaves the ref holding the
      // pre-clear snapshot, and the next unforced loadGames restores every badge from it.
      await reloadGamesOnce();
    } catch (err) {
      if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
      notifyError(t('prefill.errors.clearAllFromCacheFailed'), err, {
        logLabel: 'Failed to clear the prefill cache'
      });
    } finally {
      if (gamesEpochRef.current === epoch && gamesKeyRef.current === key)
        setIsClearingAllCache(false);
    }
  }, [reloadGamesOnce, notifyError, t, addLog, serviceId]);

  const handleRemoveFromCache = useCallback(
    async (appId: string) => {
      const epoch = gamesEpochRef.current;
      const key = gamesKeyRef.current;
      setRemovingAppId(appId);
      const gameName = ownedGames.find((g) => g.appId === appId)?.name ?? `#${appId}`;
      try {
        await ApiService.deletePrefillCachedApp(appId, serviceId);
        if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
        gamesRequestRef.current?.abort();
        gamesCacheRef.current = null;
        setCachedAppIds((previous) => previous.filter((id) => id !== appId));
        addLog('info', t('prefill.log.removedFromCache', { game: gameName }));
        // Re-read rather than trusting the broadcast to come back to this browser: with the
        // socket down, the row the user just acted on would otherwise keep its Cached badge.
        await reloadGamesOnce();
      } catch (err) {
        if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
        addLog('error', t('prefill.log.removeFromCacheFailed', { game: gameName }));
        notifyError(t('prefill.errors.removeFromCacheFailed'), err, {
          logLabel: 'Failed to remove app from prefill cache'
        });
      } finally {
        if (gamesEpochRef.current === epoch && gamesKeyRef.current === key) setRemovingAppId(null);
      }
    },
    [reloadGamesOnce, notifyError, t, ownedGames, addLog, serviceId]
  );

  const executeCommand = useCallback(
    async (commandType: CommandType) => {
      if (!signalR.session || !signalR.hubConnection.current) return;
      if (
        commandType.startsWith('clear-') &&
        (signalR.isPrefillActive || signalR.runs.some(isPrefillRunActive))
      )
        return;
      if (signalR.session.status !== 'Active' || signalR.timeRemaining <= 0) {
        signalR.setError(t('prefill.errors.sessionExpired'));
        addLog('warning', t('prefill.errors.sessionExpired'));
        return;
      }

      // Admission uses the advertised run limit, with single-run fallback for older daemons.
      const isPrefillCommand = commandType.startsWith('prefill');
      if (isPrefillCommand && !signalR.canStart) {
        addLog('warning', t('errors.prefill.runLimit'));
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
            await handleClearAllFromCache();
            break;
          }
        }
      } catch (err) {
        addLog('error', getErrorMessage(err) || t('prefill.log.commandFailed'));
        if (isPrefillCommand && supportsConcurrentPrefill(signalR.session)) {
          await signalR.refreshRuns();
        }
        // V1: roll back the OPTIMISTIC 'starting' bar painted in handleConfirmCommand. If the
        // prefill POST threw (409 already-running / network / non-ok) no daemon run started, so no
        // terminal event will ever arrive to clear it — without this the fake "Contacting daemon..."
        // bar (and its live-but-dead Cancel) sticks and the start-guard blocks all retries. Gated to
        // the prefill start path so a genuinely-running prefill's bar (which the start-guard already
        // protects from re-entry) is never clobbered.
        if (isPrefillCommand && !supportsConcurrentPrefill(signalR.session)) {
          signalR.setIsPrefillActive(false);
          signalR.setPrefillProgress(null);
          sessionStore.removeItem(STORAGE_KEYS.PREFILL_IN_PROGRESS);
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
      signalR.canStart,
      signalR.setError,
      callPrefillApi,
      selectedAppIds,
      addLog,
      loadGames,
      handleClearAllFromCache,
      t
    ]
  );

  const handleEndSession = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    addLog('info', t('prefill.log.endingSession'));
    try {
      await signalR.hubConnection.current.invoke('EndSessionAsync', signalR.session.id);
    } catch {
      // Session end failed - will be cleaned up by timeout
    }
  }, [signalR.session, signalR.hubConnection, addLog, t]);

  const handleCancelLogin = useCallback(async () => {
    if (!signalR.session || !signalR.hubConnection.current) return;

    try {
      await signalR.hubConnection.current.invoke('CancelLoginAsync', signalR.session.id);
      setShowAuthModal(false);
      authActions.resetAuthForm();
      addLog('info', t('prefill.log.loginCancelled'));
    } catch {
      // Cancel login failed
    }
  }, [signalR.session, signalR.hubConnection, authActions, addLog, t]);

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
      const epoch = gamesEpochRef.current;
      const key = gamesKeyRef.current;
      const normalizedAppIds = appIds.map((id) => String(id));

      try {
        const response = await fetch(
          `${API_BASE}/${serviceBasePath}/sessions/${signalR.session.id}/selected-apps`,
          ApiService.getJsonFetchOptions({ appIds: normalizedAppIds }, { method: 'POST' })
        );
        await assertOk(response);
        if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
        setSelectedAppIds(normalizedAppIds);
        setShowGameSelection(false);
        addLog('success', t('prefill.log.selectedGames', { count: normalizedAppIds.length }));
      } catch (err) {
        if (gamesEpochRef.current !== epoch || gamesKeyRef.current !== key) return;
        addLog('error', t('prefill.log.failedSaveSelection'));
        throw err;
      }
    },
    [signalR.session, addLog, serviceBasePath, t]
  );

  // Confirmation dialog logic
  // Resolves the estimate and reports whether the daemon has actually RESOLVED the current
  // selection yet. The daemon returns per-app rows (`apps`) once it has looked the selection up;
  // until then it answers with an empty list and a 0 total - which is "not computed yet", not a
  // real answer. Callers use `ready` to refetch precisely while it is still resolving instead of
  // blindly retrying on any 0 (a genuine 0, e.g. a fully cached or unavailable selection, comes
  // back WITH resolved rows and is accepted immediately). Returns null when it could not run at
  // all (no session / selection, hub not Connected, or the invoke threw).
  // Depend on the session id, not the session object: the object is replaced on every
  // timeRemaining tick, which would otherwise re-create this callback constantly and restart
  // the warm-estimate effect's retry chain.
  const estimateSessionId = signalR.session?.id ?? null;
  const fetchEstimatedSize = useCallback(async (): Promise<{
    bytes: number;
    ready: boolean;
  } | null> => {
    if (!estimateSessionId || !signalR.hubConnection.current || selectedAppIds.length === 0) {
      return null;
    }

    // Claim this request. `commit` only writes state while this is still the newest request,
    // so a slow invoke that resolves after the selection changed (or after a newer retry fired)
    // cannot overwrite the fresher estimate.
    const requestId = ++estimateRequestIdRef.current;
    const commit = (next: Parameters<typeof setEstimatedSize>[0]) => {
      if (estimateRequestIdRef.current === requestId) {
        setEstimatedSize(next);
      }
    };

    commit({ bytes: 0, loading: true });

    // On mobile the hub can still be (re)connecting when this first runs; invoking then
    // throws ("connection is not in the 'Connected' State") and would surface a spurious
    // "Unable to estimate size" error. Stay in the loading state and return null so the
    // caller refetches once the socket is up, instead of showing the scary error.
    if (signalR.hubConnection.current.state !== 'Connected') {
      return null;
    }

    try {
      const status = (await signalR.hubConnection.current.invoke(
        'GetSelectedAppsStatusAsync',
        estimateSessionId,
        selectedOS
      )) as {
        totalDownloadSize: number;
        message?: string;
        apps?: EstimatedSizeApp[];
      };

      const bytes = status.totalDownloadSize || 0;
      // Resolved once the daemon has returned rows for the selection. An empty list back means
      // it has not looked the selection up yet (the transient that showed "0 B" until revisit).
      const ready = (status.apps?.length ?? 0) > 0;

      if (ready) {
        // The estimate refetches on reconnect and while the daemon is still resolving, so
        // key the log entry on what was estimated to avoid repeating the same line.
        const signature = `${selectedAppIds.join(',')}|${selectedOS.join(',')}|${bytes}`;
        if (estimateLoggedRef.current !== signature) {
          estimateLoggedRef.current = signature;
          addLog('info', t('prefill.log.estimatedSizeReady', { size: formatBytes(bytes) }));
        }
        commit({
          bytes,
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
      } else {
        // Keep the spinner rather than flashing a transient "0 B" while the daemon resolves.
        commit({ bytes: 0, loading: true });
      }

      return { bytes, ready };
    } catch (err) {
      // Background size estimate - already has its own inline error slot (estimatedSize.error) that
      // the UI reads directly, so no notification is needed; log the detail for diagnosis.
      console.error('[PrefillPanel] Failed to estimate size:', getErrorMessage(err));
      commit({
        bytes: 0,
        loading: false,
        error: t('prefill.errors.unableEstimateSize')
      });
      return null;
    }
  }, [estimateSessionId, signalR.hubConnection, selectedAppIds, selectedOS, addLog, t]);

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
            message: t('prefill.confirm.downloadAllMessage', { service: serviceName })
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
    [selectedAppIds, t, serviceName]
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
        // No refetch here: the warm effect already keeps `estimatedSize` current for the
        // selection (retrying while it resolves), and the confirm modal reads that live state.
        // An uncoordinated one-shot fetch on open could stick in loading or clobber a good value.
      } else {
        executeCommand(command);
      }
    },
    [executeCommand]
  );

  const handleConfirmCommand = useCallback(() => {
    if (!pendingConfirmCommand) return;

    // Continue start-guard: short-circuit if a prefill is already running (reliable now that
    // isPrefillActive is re-hydrated from server truth) so Continue can't spawn a duplicate run.
    if (pendingConfirmCommand.startsWith('prefill') && !signalR.canStart) {
      addLog('warning', t('errors.prefill.runLimit'));
      setPendingConfirmCommand(null);
      // Keep the resolved estimate for the still-selected games; clearing it to 0 B would stick
      // (the modal no longer refetches on open, and no warm-effect dependency changes here).
      return;
    }

    // Optimistic start: paint a 'starting' bar immediately so there is no dead gap between
    // Continue and the first server PrefillProgress/PrefillStateChanged event.
    if (
      pendingConfirmCommand.startsWith('prefill') &&
      !supportsConcurrentPrefill(signalR.session)
    ) {
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
    // Do NOT clear the estimate here: the selection is unchanged, so the warm effect's value is
    // still correct. Clearing it to 0 B would stick (the modal no longer refetches on open).
  }, [pendingConfirmCommand, executeCommand, signalR, addLog, t]);

  const handleCancelConfirm = useCallback(() => {
    setPendingConfirmCommand(null);
    // Keep the resolved estimate for the still-selected games (see handleConfirmCommand).
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

  // Re-arm the estimate log for a new selection. Keyed on the selection alone, so the estimate
  // logs again after deselecting and re-picking the same games, while the reconnect and
  // readiness re-runs of the effect below still share one log line per estimate.
  useEffect(() => {
    estimateLoggedRef.current = null;
  }, [selectedAppIds, selectedOS]);

  // Warm the size estimate as the selection changes so the split card can show it without
  // waiting for the confirm modal (which reads this live state). Debounced. On mobile the hub
  // can still be (re)connecting or the server-side depot sizes still warming when this first
  // runs, which returns "not ready" and used to stick until the page was revisited; the
  // readiness-keyed refetch below self-heals it, and re-running on reconnect recovers after a
  // long outage where the bounded retries had already given up.
  useEffect(() => {
    if (!isReadyForCommands || !isSessionActive || selectedAppIds.length === 0) return;

    // Invalidate any estimate still in flight for a previous selection right now (not 400ms
    // later when the debounced fetch claims its own id) so a slow prior response cannot commit
    // over - or let the modal confirm - a stale size. Show the spinner immediately.
    estimateRequestIdRef.current += 1;
    setEstimatedSize({ bytes: 0, loading: true });

    let cancelled = false;
    let attempt = 0;
    let handle: ReturnType<typeof setTimeout>;
    const maxAttempts = 5;

    const run = async () => {
      const result = await fetchEstimatedSize();
      if (cancelled) return;
      // Refetch ONLY while the estimate is genuinely not ready - the hub was not Connected yet
      // (result === null) or the daemon has not resolved the selection yet (result.ready ===
      // false). A resolved result, even 0 bytes, is accepted immediately, so this is not a blind
      // retry-on-zero. Linear backoff, bounded, so it can never spin.
      const notReady = result === null || !result.ready;
      if (notReady && attempt < maxAttempts) {
        attempt += 1;
        handle = setTimeout(run, 800 * attempt);
      } else if (notReady) {
        // Never became ready within the window (hub stayed down / daemon never answered):
        // settle on the error instead of spinning forever.
        setEstimatedSize({
          bytes: 0,
          loading: false,
          error: t('prefill.errors.unableEstimateSize')
        });
      }
    };

    handle = setTimeout(run, 400);

    return () => {
      cancelled = true;
      clearTimeout(handle);
      // Retire this generation on teardown too, so a late response for a now-gone selection
      // (selection cleared, unmount, or reconnect re-run) can never commit.
      estimateRequestIdRef.current += 1;
    };
  }, [
    selectedAppIds,
    selectedOS,
    isReadyForCommands,
    isSessionActive,
    fetchEstimatedSize,
    t,
    signalR.isConnected
  ]);

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
            loginDeadline={authLoginDeadline}
          />
        ) : (
          <SteamAuthModal
            opened={showAuthModal}
            onClose={() => setShowAuthModal(false)}
            state={authState}
            actions={authActions}
            isPrefillMode={true}
            onCancelLogin={handleCancelLogin}
            loginDeadline={authLoginDeadline}
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
  const confirmMessage = pendingConfirmCommand
    ? getConfirmationMessage(pendingConfirmCommand)
    : null;

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
          loginDeadline={authLoginDeadline}
        />
      ) : (
        <SteamAuthModal
          opened={showAuthModal}
          onClose={() => setShowAuthModal(false)}
          state={authState}
          actions={authActions}
          isPrefillMode={true}
          onCancelLogin={handleCancelLogin}
          loginDeadline={authLoginDeadline}
        />
      )}

      {/* Game Selection Modal */}
      <GameSelectionModal
        opened={showGameSelection}
        onClose={() => setShowGameSelection(false)}
        serviceId={serviceId}
        games={ownedGames}
        selectedAppIds={selectedAppIds}
        onSave={handleSaveGameSelection}
        isLoading={isLoadingGames}
        cachedAppIds={cachedAppIds}
        unknownAppIds={unknownAppIds}
        error={gameLoadError}
        isUsingCache={isUsingGamesCache}
        onRescan={reloadGamesOnce}
        onRemoveFromCache={
          // The delete route is AccountHolder-only while the read that fills this list is not, so a
          // guest offered this control could only ever be answered with a 403.
          isAdmin ? handleRemoveFromCache : undefined
        }
        removingAppId={removingAppId}
        onClearAllCache={isAdmin ? handleClearAllFromCache : undefined}
        isClearingAllCache={isClearingAllCache}
      />

      {/* Large Prefill Confirmation Dialog */}
      <ConfirmationModal
        opened={!!pendingConfirmCommand}
        onClose={handleCancelConfirm}
        onConfirm={handleConfirmCommand}
        title={confirmMessage?.title ?? ''}
        confirmLabel={
          pendingConfirmCommand === 'prefill'
            ? t('prefill.confirm.startDownload')
            : t('prefill.confirm.yesContinue')
        }
        confirmColor="run"
        confirmDisabled={pendingConfirmCommand === 'prefill' && estimatedSize.loading}
      >
        {confirmMessage && <p className="text-sm text-themed-muted">{confirmMessage.message}</p>}

        {pendingConfirmCommand === 'prefill' && (
          <div className="p-3 rounded-lg bg-[var(--theme-bg-secondary)]">
            {estimatedSize.loading ? (
              <div className="flex items-center gap-2">
                <LoadingSpinner inline size="sm" className="text-[var(--theme-primary)]" />
                <span className="text-sm text-themed-muted">
                  {t('prefill.confirm.calculatingSize')}
                </span>
              </div>
            ) : estimatedSize.error ? (
              <span className="text-sm text-themed-muted">{estimatedSize.error}</span>
            ) : (
              <div className="space-y-2">
                <div className="flex items-center justify-between">
                  <span className="text-sm text-themed-muted">
                    {t('prefill.confirm.totalEstimated')}
                  </span>
                  <span className="text-sm font-semibold text-[var(--theme-primary)]">
                    {formatBytes(estimatedSize.bytes)}
                  </span>
                </div>
                {estimatedSize.apps && estimatedSize.apps.length > 0 && (
                  <div className="pt-2 border-t border-[var(--theme-border-primary)]">
                    <div className="text-xs text-themed-muted mb-1">
                      {t('prefill.confirm.breakdown', { count: estimatedSize.apps.length })}:
                    </div>
                    <div className="space-y-1 max-h-32 overflow-y-auto">
                      {estimatedSize.apps.map((app) => {
                        const sizeSpan = (
                          <span
                            className={`whitespace-nowrap ${
                              app.isUnsupportedOs
                                ? 'text-[var(--theme-warning)]'
                                : 'text-themed-muted'
                            }`}
                          >
                            {app.isUnsupportedOs
                              ? app.unavailableReason || t('prefill.confirm.unsupportedOs')
                              : formatBytes(app.downloadSize)}
                          </span>
                        );
                        return (
                          <div
                            key={app.appId}
                            className={`flex items-center justify-between text-xs ${
                              app.isUnsupportedOs ? 'opacity-50' : ''
                            }`}
                          >
                            <Tooltip
                              content={app.unavailableReason || app.name}
                              position="top"
                              className="flex min-w-0"
                            >
                              <span
                                className={`truncate mr-2 max-w-[200px] ${
                                  app.isUnsupportedOs
                                    ? 'text-themed-muted line-through'
                                    : 'text-themed-secondary'
                                }`}
                              >
                                {app.name}
                              </span>
                            </Tooltip>
                            {app.unavailableReason ? (
                              <Tooltip content={app.unavailableReason} position="top">
                                {sizeSpan}
                              </Tooltip>
                            ) : (
                              sizeSpan
                            )}
                          </div>
                        );
                      })}
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
        )}
      </ConfirmationModal>

      {/* Session Expired Notice */}
      {isSessionExpired && (
        <div className="p-4 rounded-lg flex flex-col sm:flex-row sm:items-center sm:justify-between gap-3 bg-[var(--theme-warning-subtle)] border border-[var(--theme-warning-strong)]">
          <div className="flex items-start gap-3">
            <AlertCircle className="h-5 w-5 flex-shrink-0 text-[var(--theme-warning)]" />
            <div>
              <p className="font-medium text-sm text-[var(--theme-warning-text)]">
                {t('prefill.sessionExpired.title')}
              </p>
              <p className="text-sm text-themed-muted">
                {t('prefill.sessionExpired.message', { service: serviceName })}
              </p>
            </div>
          </div>
          <Button
            variant="filled"
            color="run"
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
            {/* Login state moves into the header once authenticated; the standalone auth card
                only renders while a login is still required. */}
            {!isAnonymousService && signalR.isLoggedIn && (
              <p className="flex items-center gap-2 text-sm text-themed-muted">
                <span className="status-dot active" aria-hidden="true" />
                {t('prefill.auth.loggedIn', { service: serviceName })}
              </p>
            )}
          </div>
        </div>

        <div className="flex items-center gap-3 w-full sm:w-auto">
          {/* Session Timer - non-interactive, but shares Button's md padding and the 44px control
              minimum used by this action cluster. */}
          <div
            className={`prefill-session-timer inline-flex h-11 min-h-11 flex-none items-center justify-center gap-2 px-4 py-2 themed-button-radius border ${
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

          {!isSessionExpired && (
            <Button
              variant="filled"
              color="stop"
              size="md"
              onClick={handleEndSession}
              className="prefill-end-session h-11 min-h-11 flex-none"
            >
              {t('prefill.endSession')}
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

      {/* Main Content - Two Column Layout. While a job runs on stacked layouts the columns
          flatten so the activity log orders directly after the progress card (CSS in
          prefill.css); the xl grid is untouched. */}
      <div
        className={`grid grid-cols-1 xl:grid-cols-3 gap-4 prefill-layout ${
          (signalR.prefillProgress || signalR.runs.some(isPrefillRunActive)) && isSessionActive
            ? 'prefill-layout--running'
            : ''
        }`}
      >
        {/* Left Column - Controls */}
        <div className="xl:col-span-2 space-y-4 prefill-col-controls">
          {/* Authentication Card - only while a login is still required. Battle.net and Riot are
              anonymous (no login) and a completed login reports through the header status line. */}
          {!isAnonymousService && !signalR.isLoggedIn && (
            <div className="prefill-sec-auth">
              <Card padding="md">
                <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
                  <div className="flex items-center gap-3">
                    <div className="w-10 h-10 rounded-lg flex items-center justify-center bg-[var(--theme-warning-subtle)]">
                      <LogIn className="h-5 w-5 text-[var(--theme-warning)]" />
                    </div>
                    <div>
                      <p className="font-medium text-themed-primary">
                        {t('prefill.auth.loginRequired', { service: serviceName })}
                      </p>
                      <p className="text-sm text-themed-muted">
                        {t('prefill.auth.authenticateToAccess')}
                      </p>
                    </div>
                  </div>

                  {!isSessionExpired && (
                    <Button
                      variant="filled"
                      color="primary"
                      size="md"
                      onClick={handleOpenAuthModal}
                      className="flex-shrink-0 w-full sm:w-auto min-h-[44px] sm:min-h-10"
                    >
                      {t('prefill.auth.loginToService', { service: serviceName })}
                    </Button>
                  )}
                </div>
              </Card>
            </div>
          )}

          {/* Network Status Card */}
          <div className="prefill-sec-network">
            <NetworkStatusSection diagnostics={signalR.session.networkDiagnostics} />
          </div>

          {/* Background Completion Notification Banner */}
          {backgroundCompletion && !signalR.prefillProgress && signalR.runs.length === 0 && (
            <div className="prefill-sec-completion">
              <CompletionBanner
                completion={backgroundCompletion}
                onDismiss={clearBackgroundCompletion}
              />
            </div>
          )}

          {/* Download Progress Card */}
          {signalR.prefillProgress &&
            signalR.runs.length === 0 &&
            !supportsConcurrentPrefill(signalR.session) &&
            isSessionActive && (
              <div className="prefill-sec-progress">
                <PrefillProgressCard
                  progress={signalR.prefillProgress}
                  onCancel={handleCancelPrefill}
                  isCancelling={signalR.isCancellingState}
                />
              </div>
            )}

          {(signalR.runs.length > 0 ||
            runCompletions.some(
              (run) => run.sessionId === signalR.session?.id && run.notificationMode !== 'silent'
            )) && (
            <div className="prefill-sec-progress space-y-3">
              {[
                ...signalR.runs,
                ...runCompletions.filter(
                  (run) =>
                    run.sessionId === signalR.session?.id &&
                    run.notificationMode !== 'silent' &&
                    !signalR.runs.some(
                      (current) =>
                        current.runId === run.runId &&
                        current.daemonInstanceId === run.daemonInstanceId
                    )
                )
              ]
                .sort(
                  (a, b) =>
                    a.snapshot.startedAt.localeCompare(b.snapshot.startedAt) ||
                    a.runId.localeCompare(b.runId)
                )
                .map((run) => (
                  <PrefillProgressCard
                    key={`${run.sessionId}:${run.daemonInstanceId}:${run.runId}`}
                    run={run}
                    progress={getPrefillRunProgress(run)}
                    onCancel={() => void signalR.cancelPrefill(run.runId)}
                    isCancelling={run.cancelRequested}
                    error={signalR.runErrors[run.runId]}
                    disabled={!isSessionActive}
                  />
                ))}
            </div>
          )}

          {/* Command Buttons */}
          <div className="prefill-sec-commands">
            <PrefillCommandButtons
              isLoggedIn={isReadyForCommands}
              isExecuting={isExecuting}
              isPrefillActive={signalR.isPrefillActive || signalR.runs.some(isPrefillRunActive)}
              canStart={signalR.canStart}
              activeRunCount={signalR.runs.filter(isPrefillRunActive).length}
              maxConcurrentRuns={
                supportsConcurrentPrefill(signalR.session)
                  ? signalR.session.maxConcurrentRuns
                  : undefined
              }
              isSessionActive={isSessionActive}
              isUserAuthenticated={isAdmin}
              selectedAppIds={selectedAppIds}
              selectedOS={selectedOS}
              maxConcurrency={maxConcurrency}
              maxThreadLimit={maxThreadLimit}
              supportedCommands={serviceConfig.prefillCommands}
              supportedOperatingSystems={serviceConfig.supportedOperatingSystems}
              cachedAppIds={cachedAppIds}
              estimatedSize={estimatedSize}
              onCommandClick={handleCommandClick}
              onSelectedOSChange={handleOSChange}
              onMaxConcurrencyChange={handleConcurrencyChange}
            />
          </div>
        </div>

        {/* Right Column - Activity Log */}
        <div className="xl:col-span-1 prefill-col-log">
          <Card padding="none" className="overflow-hidden prefill-log-card">
            <div className="px-4 pt-4 pb-3 flex items-center gap-3 border-b border-[var(--theme-border-primary)]">
              <div className="w-8 h-8 rounded-lg flex items-center justify-center flex-shrink-0 bg-[var(--theme-accent-subtle)]">
                <ScrollText className="h-4 w-4 text-[var(--theme-accent)]" />
              </div>
              <div>
                <h3 className="text-base font-semibold text-themed-primary">
                  {t('prefill.activityLog.title')}
                </h3>
              </div>
            </div>
            <CardContent className="p-0">
              <ActivityLog entries={logEntries} serviceId={serviceId} nested />
            </CardContent>
          </Card>
        </div>
      </div>
    </div>
  );
}
