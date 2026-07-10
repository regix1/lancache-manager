import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { HelpPopover } from '@components/ui/HelpPopover';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import ApiService from '@services/api.service';
import { GameSelectionModal } from '@components/features/prefill/GameSelectionModal';
import { NumberInput } from '@components/ui/NumberInput';
import {
  PERSISTENT_PREFILL_SERVICES,
  PERSISTENT_PREFILL_VALIDITY_BOUNDS
} from '@components/features/prefill/persistentPrefillConstants';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_BUTTON_SIZE,
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER,
  SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS,
  SCHEDULED_PREFILL_SUPPORTED_PRESETS,
  SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS
} from './constants';
import { ScheduledPrefillPlatformsPanel } from './ScheduledPrefillPlatformsPanel';
import {
  getPersistentServiceId,
  isScheduledPrefillAnonymousService,
  needsPersistentLogin
} from './scheduledPrefillPlatformUi';
import { PersistentLoginHost } from './PersistentLoginHost';
import { normalizePersistentLoginClearResults } from './persistentLoginClearResult';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import {
  getPersistentLoginSessionId,
  hasActivePersistentLogin,
  isPersistentLoginDismissed,
  reconcilePersistentLoginFromServer,
  requestPersistentLoginAttempt,
  resetPersistentLoginState
} from './persistentLoginStore';
import { usePersistentPrefillContainerSignalR } from './usePersistentPrefillContainerSignalR';
import { usePersistentLoginChallengeSignalR } from './usePersistentLoginChallengeSignalR';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillOperatingSystem,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';
import { getErrorMessage, isAbortError } from '@utils/error';
import { useTimeoutCallback } from '@/hooks/useTimeoutCallback';

interface ScheduledPrefillConfigModalProps {
  opened: boolean;
  onClose: () => void;
  onSaved?: () => void | Promise<void>;
}

interface ScheduledPrefillOwnedGame {
  appId: string;
  name: string;
}

interface ScheduledPrefillGameSelectionState {
  serviceKey: ScheduledPrefillServiceKey;
  sessionId: string;
  games: ScheduledPrefillOwnedGame[];
  cachedAppIds: string[];
}

interface NumericBounds {
  min: number;
  max: number;
}

const DEFAULT_PERSISTENT_PREFILL_VALIDITY_DAYS = 90;

const mapOperatingSystems = (
  operatingSystems: ScheduledPrefillOperatingSystem[]
): string[] | undefined => {
  if (operatingSystems.length === 0) {
    return undefined;
  }

  return operatingSystems.map((os) => {
    switch (os) {
      case 'Windows':
        return 'windows';
      case 'Linux':
        return 'linux';
      case 'Macos':
        return 'macos';
    }
  });
};

const clampToBounds = (value: number, bounds: NumericBounds): number =>
  Math.min(bounds.max, Math.max(bounds.min, Math.trunc(value)));

const reconcileServiceConfigPreset = (
  serviceKey: ScheduledPrefillServiceKey,
  serviceConfig: ScheduledPrefillServiceConfigDto
): ScheduledPrefillServiceConfigDto => {
  if (SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes(serviceConfig.preset)) {
    return serviceConfig;
  }

  // A config saved before this service's preset options were capability-gated (or written
  // directly via the API) can carry a preset this service no longer offers. Fall back to 'All' at
  // load time so the segmented control always shows a valid active selection instead of nothing.
  return { ...serviceConfig, preset: 'All', topCount: null };
};

const reconcileServiceConfigOperatingSystems = (
  serviceKey: ScheduledPrefillServiceKey,
  serviceConfig: ScheduledPrefillServiceConfigDto
): ScheduledPrefillServiceConfigDto => {
  const supportedOperatingSystems = SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS[serviceKey];
  if (serviceConfig.operatingSystems.every((os) => supportedOperatingSystems.includes(os))) {
    return serviceConfig;
  }

  // A config saved before this service's platform selection was capability-gated (or written
  // directly via the API) can carry OS values this service no longer supports. Drop them at load
  // time so the (now-hidden, for this service) field never resurfaces an unsupported selection.
  return {
    ...serviceConfig,
    operatingSystems: serviceConfig.operatingSystems.filter((os) =>
      supportedOperatingSystems.includes(os)
    )
  };
};

const reconcileServiceConfig = (
  serviceKey: ScheduledPrefillServiceKey,
  serviceConfig: ScheduledPrefillServiceConfigDto
): ScheduledPrefillServiceConfigDto =>
  reconcileServiceConfigOperatingSystems(
    serviceKey,
    reconcileServiceConfigPreset(serviceKey, serviceConfig)
  );

const reconcileScheduledPrefillConfig = (
  rawConfig: ScheduledPrefillConfigDto
): ScheduledPrefillConfigDto => ({
  ...rawConfig,
  steam: reconcileServiceConfig('steam', rawConfig.steam),
  epic: reconcileServiceConfig('epic', rawConfig.epic),
  xbox: reconcileServiceConfig('xbox', rawConfig.xbox),
  battleNet: reconcileServiceConfig('battleNet', rawConfig.battleNet),
  riot: reconcileServiceConfig('riot', rawConfig.riot)
});

const validateServiceConfig = (
  serviceConfig: ScheduledPrefillServiceConfigDto,
  serviceKey: ScheduledPrefillServiceKey,
  serviceName: string,
  t: (key: string, values?: Record<string, string | number>) => string
): string | null => {
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  if (!serviceConfig.enabled) {
    return null;
  }

  // Defense-in-depth: config is reconciled at load time, so this should not normally trigger, but
  // it guarantees an unsupported preset+service combination can never be silently re-saved.
  if (!SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes(serviceConfig.preset)) {
    return t(`${baseKey}.validation.unsupportedPreset`, {
      service: serviceName,
      preset: t(`${baseKey}.presets.${serviceConfig.preset.toLowerCase()}`)
    });
  }

  if (serviceConfig.preset === 'Top' && (!serviceConfig.topCount || serviceConfig.topCount < 1)) {
    return t(`${baseKey}.validation.topCount`, { service: serviceName });
  }

  // Services with no supported operating systems have the field hidden entirely, so they can
  // never populate this list - only require a selection where the field is actually shown.
  if (
    SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS[serviceKey].length > 0 &&
    serviceConfig.operatingSystems.length === 0
  ) {
    return t(`${baseKey}.validation.operatingSystems`, { service: serviceName });
  }

  if (
    serviceConfig.maxConcurrency.mode === 'Fixed' &&
    (serviceConfig.maxConcurrency.value < SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min ||
      serviceConfig.maxConcurrency.value > SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max)
  ) {
    return t(`${baseKey}.validation.maxConcurrency`, {
      service: serviceName,
      min: SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min,
      max: SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max
    });
  }

  return null;
};

export function ScheduledPrefillConfigModal({
  opened,
  onClose,
  onSaved
}: ScheduledPrefillConfigModalProps) {
  const { t } = useTranslation();
  // CustomScrollbar's own maxHeight="100%" does not reliably resolve here: this modal's body is a
  // pure flex-grow chain with no explicit CSS height anywhere in it, and a plain block descendant's
  // percentage max-height needs one (measured: it fell back to unconstrained content height instead
  // of clamping). Measuring the wrapper's actual pixel height and passing that as a concrete value
  // sidesteps the percentage-resolution question entirely.
  //
  // A callback ref (not useRef + an effect keyed on `opened`) because Modal mounts this element
  // one render AFTER `opened` flips true (it only renders `children` once its own internal
  // `isVisible` state catches up) - an effect keyed on `opened` fires too early, finds the ref
  // still null, bails out, and never gets a second chance since `opened` doesn't change again.
  // The callback ref fires exactly when the node mounts, whenever that actually happens.
  const [scrollAreaEl, setScrollAreaEl] = useState<HTMLDivElement | null>(null);
  const [scrollAreaHeight, setScrollAreaHeight] = useState<number | null>(null);
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  // null = never loaded yet (drives the initial-load-only spinner below); an empty array means a
  // load completed and found no containers, which is a real, renderable state, not "loading".
  const [persistentContainers, setPersistentContainers] = useState<
    PersistentPrefillContainerDto[] | null
  >(null);
  const [loadingPersistentContainers, setLoadingPersistentContainers] = useState(false);
  const [persistentError, setPersistentError] = useState<string | null>(null);
  const [persistentAction, setPersistentAction] =
    useState<ScheduledPrefillPersistentActionState | null>(null);
  const [persistentValidityDays, setPersistentValidityDays] = useState(
    DEFAULT_PERSISTENT_PREFILL_VALIDITY_DAYS
  );
  const [loadingGlobalSettings, setLoadingGlobalSettings] = useState(false);
  const [globalSettingsError, setGlobalSettingsError] = useState<string | null>(null);
  const [clearLoginsConfirmOpen, setClearLoginsConfirmOpen] = useState(false);
  const [clearingLogins, setClearingLogins] = useState(false);
  const [clearLoginsSuccessNote, setClearLoginsSuccessNote] = useState<string | null>(null);
  const [gameSelection, setGameSelection] = useState<ScheduledPrefillGameSelectionState | null>(
    null
  );
  const [loadingGameSelectionService, setLoadingGameSelectionService] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const [gameSelectionError, setGameSelectionError] = useState<string | null>(null);
  const [persistentLoginTarget, setPersistentLoginTarget] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  // Auto-dismiss the "logins cleared" success note so it does not linger forever.
  const scheduleClearLoginsSuccessDismiss = useTimeoutCallback(2500);

  const loadConfig = useCallback(async (signal?: AbortSignal) => {
    setLoadingConfig(true);
    try {
      const nextConfig = await ApiService.getScheduledPrefillConfig(signal);
      setConfig(reconcileScheduledPrefillConfig(nextConfig));
      setLoadError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setLoadError(getErrorMessage(error));
      }
    } finally {
      setLoadingConfig(false);
    }
  }, []);

  // Single-flight: the click-handler path (runPersistentStartFlow etc.) and the SignalR onRefresh
  // subscription both call this independently, and used to double-fire back-to-back on the same
  // click, each toggling loadingPersistentContainers and producing two visible flicker cycles
  // (diagnostic §3 step 2). A concurrent second call now just joins the first's in-flight promise
  // instead of starting a redundant fetch - kept as a join rather than dropping either caller's
  // load, since the manual call is still the only path when SignalR is disconnected.
  const persistentContainersRequestRef = useRef<Promise<void> | null>(null);

  const loadPersistentContainers = useCallback(async (signal?: AbortSignal) => {
    if (persistentContainersRequestRef.current) {
      return persistentContainersRequestRef.current;
    }

    const request = (async () => {
      setLoadingPersistentContainers(true);
      try {
        const nextContainers = await ApiService.getPersistentPrefillContainers(signal);
        setPersistentContainers(nextContainers);
        setPersistentError(null);
      } catch (error: unknown) {
        if (!isAbortError(error)) {
          setPersistentError(getErrorMessage(error));
        }
      } finally {
        setLoadingPersistentContainers(false);
      }
    })();

    persistentContainersRequestRef.current = request;
    try {
      await request;
    } finally {
      persistentContainersRequestRef.current = null;
    }
  }, []);

  const loadGlobalSettings = useCallback(async (signal?: AbortSignal) => {
    setLoadingGlobalSettings(true);
    try {
      const validity = await ApiService.getPersistentPrefillValidity(signal);
      setPersistentValidityDays(clampToBounds(validity.days, PERSISTENT_PREFILL_VALIDITY_BOUNDS));
      setGlobalSettingsError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setGlobalSettingsError(getErrorMessage(error));
      }
    } finally {
      setLoadingGlobalSettings(false);
    }
  }, []);

  useEffect(() => {
    if (!scrollAreaEl) {
      return;
    }

    const updateHeight = () => setScrollAreaHeight(scrollAreaEl.clientHeight);
    updateHeight();

    const resizeObserver = new ResizeObserver(updateHeight);
    resizeObserver.observe(scrollAreaEl);
    return () => resizeObserver.disconnect();
  }, [scrollAreaEl]);

  useEffect(() => {
    if (!opened) {
      return;
    }

    const controller = new AbortController();
    setValidationError(null);
    setSaveError(null);
    setPersistentError(null);
    setGlobalSettingsError(null);
    setGameSelectionError(null);
    setGameSelection(null);
    void loadConfig(controller.signal);
    void loadPersistentContainers(controller.signal);
    void loadGlobalSettings(controller.signal);

    return () => {
      controller.abort();
    };
  }, [opened, loadConfig, loadPersistentContainers, loadGlobalSettings]);

  const persistentContainerByService = useMemo(
    () =>
      new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
        (persistentContainers ?? []).map((container) => [container.service, container])
      ),
    [persistentContainers]
  );

  // Always-current mirror of the container map for effects that must READ it without re-running on
  // every change to it (see the reconcile effect below).
  const persistentContainerByServiceRef = useRef(persistentContainerByService);
  persistentContainerByServiceRef.current = persistentContainerByService;

  // Always-current mirror of `config` so the reconcile effect can read a service's `enabled` flag
  // without depending on the whole config object (which changes on every keystroke). Mirrors
  // persistentContainerByServiceRef; the reconcile effect re-runs on persistentEnabledSignature
  // (below) when an enabled toggle actually flips.
  const configRef = useRef(config);
  configRef.current = config;

  // Stable signature of ONLY the account services' enabled flags. Added to the reconcile effect's
  // deps so it re-checks for a resumable login when a service is enabled/disabled, WITHOUT re-firing
  // on the unrelated config edits (presets, selected app ids) a raw `config` dependency would catch.
  const persistentEnabledSignature = useMemo(
    () =>
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map((serviceKey) =>
        config?.[serviceKey].enabled ? '1' : '0'
      ).join(''),
    [config]
  );

  // Stable signature of ONLY the fields the reconcile effect branches on (running / authenticated /
  // session). The container list is refreshed on every container SignalR push - including the rapid
  // prefill-progress pushes that only move byte counters - and each refresh makes a brand-new array,
  // so keying the reconcile effect off the whole map made it re-probe the daemon (GET challenge) on
  // every progress tick. This signature only changes when a reconcile-relevant field actually
  // changes, so a progress-only refresh no longer re-fires the probe.
  const persistentReconcileSignature = useMemo(
    () =>
      (persistentContainers ?? [])
        .map(
          (container) =>
            `${container.service}:${container.isRunning ? 1 : 0}:${
              container.isAuthenticated ? 1 : 0
            }:${container.sessionId ?? ''}`
        )
        .join('|'),
    [persistentContainers]
  );

  // Reconcile instead of wipe: reopening the modal used to clear persistentLoginTarget
  // unconditionally, killing a visible auth modal and abandoning an in-progress login
  // (diagnostic §6 item 1). Once the container list is loaded, ask the backend (which caches the
  // pending challenge - see PersistentPrefillController) whether any running-but-unauthenticated
  // account service still has a login in flight, and resume it instead of losing it.
  //
  // Only skipped while the CURRENT target still has a real flow in progress (loading or an
  // already-applied challenge) - it must still run when a target is set but the store is empty
  // (the wedged state from diagnostic §3.2 W1: start() settled without ever applying the daemon's
  // challenge), otherwise a challenge cached backend-side could never reach the store again.
  //
  // Two guards keep this repair from overreaching:
  // - When a target is already set, only that service is probed - repair it in place. Scanning
  //   every account service here (as if no target were set) could find a DIFFERENT service's
  //   cached challenge first and steal the target away from the one the user is actually on.
  // - A challenge whose store entry is `dismissed` stays closed. Reconcile exists to restore state
  //   lost to a reload/unmount, not to reopen a modal the user just closed; only the explicit Log
  //   in click (which calls resumeModal via beginLogin's hasChallenge branch) reopens that one.
  useEffect(() => {
    if (!opened) {
      return;
    }

    if (
      persistentLoginTarget !== null &&
      hasActivePersistentLogin(getPersistentServiceId(persistentLoginTarget))
    ) {
      return;
    }

    const controller = new AbortController();

    const reconcile = async () => {
      const candidateKeys =
        persistentLoginTarget !== null
          ? [persistentLoginTarget]
          : SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS;

      for (const serviceKey of candidateKeys) {
        if (controller.signal.aborted) {
          return;
        }

        const serviceId = getPersistentServiceId(serviceKey);
        // A disabled (or not-yet-loaded) service must never drive an account login target: don't
        // even probe the daemon for a cached challenge, and never resurrect a stale one (bug #2 -
        // reconcile used to set a target for any running-unauthenticated account container
        // regardless of whether the current schedule enables it). configRef is read (not `config`)
        // so this effect need not depend on the whole config object; persistentEnabledSignature in
        // the dep array re-runs it when an enabled toggle actually flips.
        if (!configRef.current?.[serviceKey].enabled) {
          continue;
        }
        // Read through the ref (not the closed-over map) so this effect can depend on the reconcile
        // signature alone - the reconcile-relevant fields it reads here are exactly what that
        // signature tracks, so the ref is guaranteed current for them.
        const container = persistentContainerByServiceRef.current.get(serviceId);
        if (!container?.isRunning || container.isAuthenticated) {
          continue;
        }

        const result = await reconcilePersistentLoginFromServer(serviceId, container.sessionId);
        if (controller.signal.aborted) {
          return;
        }

        if (result === 'challenge' && !isPersistentLoginDismissed(serviceId)) {
          setPersistentLoginTarget(serviceKey);
          return;
        }
      }
    };

    void reconcile();

    return () => {
      controller.abort();
    };
    // Depends on the reconcile signature, NOT the whole container map, so a prefill-progress refresh
    // (byte counters only) no longer re-fires a daemon GET-challenge probe. persistentContainerByService
    // and config are read via refs inside, so they are intentionally not dependencies;
    // persistentEnabledSignature re-runs this only when an account service's enabled flag flips
    // (e.g. config finishing its initial load), so a just-enabled service's login can still resume.
  }, [opened, persistentLoginTarget, persistentReconcileSignature, persistentEnabledSignature]);

  // Every service (account or anonymous) reuses the same addressable persistent-container session
  // (ScheduledPrefillService.RunServiceAsync dispatches identically for all five platforms), so the
  // container map is keyed off the full run order, not just the account services.
  const containersByServiceKey = useMemo(() => {
    const map = new Map<ScheduledPrefillServiceKey, PersistentPrefillContainerDto>();
    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const serviceId = getPersistentServiceId(serviceKey);
      const container = persistentContainerByService.get(serviceId);
      if (container) {
        map.set(serviceKey, container);
      }
    }
    return map;
  }, [persistentContainerByService]);

  const selectedGamesCountByServiceKey = useMemo(() => {
    const counts = {} as Record<ScheduledPrefillServiceKey, number>;
    if (!config) {
      for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
        counts[serviceKey] = 0;
      }
      return counts;
    }

    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      counts[serviceKey] = config[serviceKey].selectedAppIds.length;
    }
    return counts;
  }, [config]);

  // Persistent per-service debounce timers for the cleanup effect below, keyed outside of render so
  // a re-run of the effect (every container-list refresh) never restarts a countdown already in
  // flight - see the effect's own comment for why restarting on every refresh would leak forever.
  const stopCleanupTimersRef = useRef<
    Map<PersistentPrefillServiceId, ReturnType<typeof setTimeout>>
  >(new Map());

  useEffect(
    () => () => {
      stopCleanupTimersRef.current.forEach((timer) => clearTimeout(timer));
      stopCleanupTimersRef.current.clear();
    },
    []
  );

  // Authoritative cleanup driven by the live container list (SignalR refresh or the initial load):
  // a service whose container has become authenticated, OR has stopped/disappeared, can never
  // legitimately resume a login, so its persistentLoginStore state (pendingChallenge/loading/
  // dismissed/challenge flags) must be dropped here - this restores the cleanup role the old
  // unconditional reopen-wipe used to provide (stale "authenticating" badges, abandoned-flow flags)
  // without losing the resumability the wipe's removal was for.
  // Resume (the reconcile effect above) is only ever offered for running+unauthenticated+pending -
  // this effect is what retires everything else, including clearing a stale persistentLoginTarget
  // that pointed at one of these services so the auth modal can never reopen on an unresumable login.
  //
  // "Stopped/missing" is reported by the very same container-list refresh that PersistentLoginHost's
  // own TRANSIENT_STOP_GRACE_MS grace period exists to tolerate (a transient blip, not a real stop).
  // Wiping the store immediately here would destroy hasActiveLogin's source data before that grace
  // timer ever gets to matter, defeating it from the sibling effect instead of the login host's own
  // unmount logic. So a service with a login actively in flight (loading or a pending challenge) gets
  // the same grace period mirrored here via a debounced reset instead of an immediate one - the login
  // host only ever unmounts on a real stop, it never resets this module-level store itself, so nothing
  // else would otherwise clean up a service that really did stop while a login was in flight.
  useEffect(() => {
    if (!opened) {
      return;
    }

    const timers = stopCleanupTimersRef.current;

    const clearPendingTimer = (serviceId: PersistentPrefillServiceId) => {
      const pending = timers.get(serviceId);
      if (pending) {
        clearTimeout(pending);
        timers.delete(serviceId);
      }
    };

    const retire = (
      serviceKey: ScheduledPrefillServiceKey,
      serviceId: PersistentPrefillServiceId
    ) => {
      resetPersistentLoginState(serviceId);
      setPersistentLoginTarget((current) => (current === serviceKey ? null : current));
    };

    for (const service of PERSISTENT_PREFILL_SERVICES) {
      const container = persistentContainerByService.get(service.service);
      const authenticatedElsewhere = container?.isRunning && container.isAuthenticated;
      const stoppedOrMissing = !container?.isRunning;

      if (!authenticatedElsewhere && !stoppedOrMissing) {
        // Running and not authenticated: a legitimate in-flight login. Cancel any stop-cleanup
        // countdown started by an earlier (transient) refresh.
        clearPendingTimer(service.service);
        continue;
      }

      if (authenticatedElsewhere) {
        clearPendingTimer(service.service);
        retire(service.key, service.service);
        continue;
      }

      // stoppedOrMissing from here on.
      if (!hasActivePersistentLogin(service.service)) {
        clearPendingTimer(service.service);
        retire(service.key, service.service);
        continue;
      }

      if (!timers.has(service.service)) {
        const timer = setTimeout(() => {
          timers.delete(service.service);
          retire(service.key, service.service);
        }, SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS);
        timers.set(service.service, timer);
      }
    }
  }, [opened, persistentContainerByService]);

  const shouldWatchPersistentAuth = useMemo(
    () =>
      (persistentContainers ?? []).some(
        (container) => container.isRunning && !container.isAuthenticated
      ) || persistentLoginTarget !== null,
    [persistentContainers, persistentLoginTarget]
  );

  // Broader than shouldWatchPersistentAuth: the container-list refresh this drives is also the
  // only thing that ever picks up a running download's live totalBytesTransferred/isPrefilling -
  // the backend broadcasts DaemonSessionUpdated on every progress tick specifically for this
  // (PrefillDaemonServiceBase.Notifications.cs), but with the auth-only condition this listener
  // went silent the moment a service finished logging in, freezing the card's progress at whatever
  // the one-shot post-start refresh had captured (usually 0 bytes) for the rest of the download.
  const shouldWatchPersistentContainers = useMemo(
    () =>
      shouldWatchPersistentAuth ||
      (persistentContainers ?? []).some((container) => container.isPrefilling),
    [shouldWatchPersistentAuth, persistentContainers]
  );

  usePersistentPrefillContainerSignalR({
    enabled: opened && shouldWatchPersistentContainers,
    onRefresh: () => {
      void loadPersistentContainers();
    }
  });

  // Event-driven challenge delivery: writes straight into persistentLoginStore the instant the
  // daemon emits a challenge, so the modal opens without waiting on the REST poll in
  // usePersistentPrefillAuth (which stays wired as the fallback for when SignalR is down).
  // Gated on shouldWatchPersistentAuth ALONE (not `opened &&`): a keep-pending login must keep
  // receiving challenge pushes while the Configure modal is closed, since ScheduledPrefillConfigModal
  // itself stays mounted and persistentLoginTarget survives the close - the hook already
  // unsubscribes on its own once nothing is pending/watched (shouldWatchPersistentAuth flips
  // false), so this cannot leak a subscription once a login truly ends.
  usePersistentLoginChallengeSignalR({
    enabled: shouldWatchPersistentAuth,
    containersByService: persistentContainerByService
  });

  // Reflects the REAL login-flow state (store loading/pendingChallenge) instead of click-time
  // bookkeeping, so a settled flow (success, failure, or an empty response) can never leave a
  // service stuck showing "Authenticating..." with its Log in button disabled (diagnostic §6/§7
  // issue 3). Recomputed on every container-list refresh, which keeps running continuously while
  // any account service sits running-but-unauthenticated (see shouldWatchPersistentAuth above) -
  // bounding staleness to "within one refresh" per the acceptance criteria.
  //
  // A dismissed-but-still-pending challenge (the user closed the auth modal via X/backdrop/Escape,
  // which keeps the daemon login and its challenge alive - see persistentLoginStore's `dismissed`
  // flag) does NOT count as "authenticating" here: `hasActivePersistentLogin` stays true for it
  // (pendingChallenge is intentionally untouched by a soft dismiss), so without this exclusion the
  // card's Log in button would stay disabled forever with no way back into the modal.
  const authenticatingServiceKeys = useMemo(
    () =>
      SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.filter((serviceKey) => {
        const serviceId = getPersistentServiceId(serviceKey);
        const container = persistentContainerByService.get(serviceId);
        return (
          container?.isRunning &&
          !container.isAuthenticated &&
          hasActivePersistentLogin(serviceId) &&
          !isPersistentLoginDismissed(serviceId)
        );
      }),
    [persistentContainerByService]
  );

  const isLoading = loadingConfig;
  const hasInitialData = config !== null;

  // Keep-previous-data: once the container list has loaded once, a background refresh must never
  // swap the persistent card's body (or the nav hints) back to a loading placeholder (diagnostic
  // §3 fix direction) - only the very first load, before any data has ever arrived, shows it.
  const isInitialPersistentContainersLoad =
    loadingPersistentContainers && persistentContainers === null;

  const validationMessage = useMemo(() => {
    if (!config) {
      return null;
    }

    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const serviceName = t(`${baseKey}.services.${serviceKey}`);
      const error = validateServiceConfig(config[serviceKey], serviceKey, serviceName, t);
      if (error) {
        return error;
      }
    }

    return null;
  }, [config, t, baseKey]);

  const enabledCount = useMemo(
    () =>
      config
        ? SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter((serviceKey) => config[serviceKey].enabled)
            .length
        : 0,
    [config]
  );

  const hasPersistentLoginWarning = useMemo(() => {
    // Config and the persistent-container list load via independent requests, so config can
    // resolve before the container list has: don't flag a false "needs login" warning while the
    // container list is still loading (or failed to load), since we simply don't know its state yet.
    if (!config || loadingPersistentContainers || persistentError) {
      return false;
    }

    return SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.some((serviceId) => {
      if (!config[serviceId].enabled) {
        return false;
      }

      const container = persistentContainerByService.get(getPersistentServiceId(serviceId));
      return needsPersistentLogin(container);
    });
  }, [config, persistentContainerByService, loadingPersistentContainers, persistentError]);

  // Single most-severe banner: errors win over the (yellow) validation hint; success is silent.
  const banner = useMemo<{ color: 'red' | 'yellow' | 'green'; message: string } | null>(() => {
    if (loadError) {
      return { color: 'red', message: t(`${baseKey}.loadError`, { error: loadError }) };
    }
    if (globalSettingsError) {
      return {
        color: 'red',
        message: t(`${baseKey}.settings.error`, { error: globalSettingsError })
      };
    }
    if (saveError) {
      return { color: 'red', message: t(`${baseKey}.saveError`, { error: saveError }) };
    }
    if (persistentError) {
      return {
        color: 'red',
        message: t(`${baseKey}.persistentContainer.error`, { error: persistentError })
      };
    }
    if (gameSelectionError) {
      return {
        color: 'red',
        message: t(`${baseKey}.selectedGames.error`, { error: gameSelectionError })
      };
    }
    if (validationError) {
      return { color: 'yellow', message: validationError };
    }
    // Success notices win over nothing (every error case above already returned), so these are
    // safe to check last - one shared horizontal banner instead of a message beside each button.
    if (clearLoginsSuccessNote) {
      return { color: 'green', message: clearLoginsSuccessNote };
    }
    return null;
  }, [
    loadError,
    globalSettingsError,
    saveError,
    persistentError,
    gameSelectionError,
    validationError,
    clearLoginsSuccessNote,
    t,
    baseKey
  ]);

  // The banner is the only surface for several one-shot errors (e.g. a failed selection sync),
  // so it must be dismissible: clear every source it can display, whichever one is showing.
  const dismissBanner = () => {
    setLoadError(null);
    setGlobalSettingsError(null);
    setSaveError(null);
    setPersistentError(null);
    setGameSelectionError(null);
    setValidationError(null);
    setClearLoginsSuccessNote(null);
  };

  const handleServiceChange = (
    serviceKey: ScheduledPrefillServiceKey,
    serviceConfig: ScheduledPrefillServiceConfigDto
  ) => {
    setConfig((current) => (current ? { ...current, [serviceKey]: serviceConfig } : current));
    setValidationError(null);
    setSaveError(null);
  };

  // Bulk enable/disable alongside the per-service toggles (not a replacement for them) - flips
  // every service's `enabled` flag in one setConfig call so it's a single undo step, not five.
  const handleSetAllServicesEnabled = (enabled: boolean) => {
    setConfig((current) => {
      if (!current) {
        return current;
      }
      const next = { ...current };
      for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
        next[serviceKey] = { ...next[serviceKey], enabled };
      }
      return next;
    });
    setValidationError(null);
    setSaveError(null);
  };

  const handlePersistentValidityDaysChange = (value: number) => {
    setPersistentValidityDays(clampToBounds(value, PERSISTENT_PREFILL_VALIDITY_BOUNDS));
    setGlobalSettingsError(null);
  };

  // Wipes stored logins for every persistent-container service in one shot: logs out any running
  // container and removes the saved credentials of stopped ones. Runs after an explicit confirm
  // (the action is destructive and requires fresh logins afterwards for every account service).
  // Local login-flow state is reset for every service regardless of the per-service outcome, since
  // none of them can have a login worth resuming once the backend has torn the credentials down.
  const handleConfirmClearLogins = async () => {
    setClearingLogins(true);
    setPersistentError(null);
    setClearLoginsSuccessNote(null);

    try {
      const response = await ApiService.clearPersistentLogins();
      const results = normalizePersistentLoginClearResults(response);

      for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
        resetPersistentLoginState(getPersistentServiceId(serviceKey));
      }
      setPersistentLoginTarget(null);

      const failedResults = results.filter((result) => result.outcome === 'failed');
      if (failedResults.length > 0) {
        setPersistentError(
          t(`${baseKey}.settings.clearLogins.partialFailure`, {
            failedCount: failedResults.length,
            total: results.length,
            services: failedResults.map((result) => result.service).join(', ')
          })
        );
      } else {
        setClearLoginsSuccessNote(t(`${baseKey}.settings.clearLogins.success`));
        scheduleClearLoginsSuccessDismiss(() => setClearLoginsSuccessNote(null));
      }

      setClearLoginsConfirmOpen(false);
      await loadPersistentContainers();
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
      setClearLoginsConfirmOpen(false);
    } finally {
      setClearingLogins(false);
    }
  };

  // Starts (or restarts, for logout) the container and refreshes the list. Shared by
  // handleStartPersistent and handleLogoutPersistent so both land in the same "running, not
  // logged in" state via a single code path - neither ever initiates a login itself; only the
  // explicit Log in click (handlePersistentLogin) or a resumed cached challenge (reconcile effect)
  // does that (diagnostic §2/§8 fix 1).
  const runPersistentStartFlow = async (serviceKey: ScheduledPrefillServiceKey) => {
    const serviceId = getPersistentServiceId(serviceKey);
    // A stopped container has no live daemon session, so any challenge left over from before it
    // was stopped is stale and must never be resumed against the new one.
    resetPersistentLoginState(serviceId);
    // The start POST already resolves once the container's daemon socket is connected (i.e. once
    // it is running - diagnostic §2), so a single refresh is enough to reflect the new state; no
    // bounded wait is needed here.
    await ApiService.startPersistentPrefillContainer(serviceId);
    await loadPersistentContainers();
    setPersistentError(null);
  };

  const handleStartPersistent = async (serviceKey: ScheduledPrefillServiceKey) => {
    setPersistentAction({ serviceKey, action: 'start' });
    setPersistentError(null);

    try {
      await runPersistentStartFlow(serviceKey);
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
  };

  const handleStopPersistent = async (serviceKey: ScheduledPrefillServiceKey) => {
    const container = persistentContainerByService.get(getPersistentServiceId(serviceKey));
    if (!container) {
      return;
    }

    setPersistentAction({ serviceKey, action: 'stop' });
    setPersistentError(null);

    try {
      await ApiService.stopPersistentPrefillContainer(container.sessionId);
      // The daemon session is gone - drop any pending challenge so a later start never tries to
      // resume a login that belonged to this now-dead session.
      resetPersistentLoginState(getPersistentServiceId(serviceKey));
      await loadPersistentContainers();
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
  };

  // Manual logout: asks the daemon to forget its stored account in place first, landing back at
  // "running, not logged in" with no restart needed. A genuine failure (socket error, timeout) falls
  // back to the existing stop+restart flow - which restarts the container but does NOT actually
  // forget the account (its named auth volume survives a restart) - so the fallback surfaces a
  // notice telling the admin their daemon image needs updating. Note an un-updated steam/epic image
  // still reports success here (it tears the live session down, just doesn't delete the account
  // file yet), so this fallback only ever triggers on a true failure, not on "unsupported command".
  // Either branch also closes an open login modal for this service immediately - logging out while a
  // login prompt is showing must not leave a stale modal open against an account that was just
  // forgotten (or is about to be stopped/restarted).
  //
  // A login still mid-challenge (not yet authenticated) is a separate case, checked first: the
  // daemon's "logout" command rejects any pre-login session outright on some daemon images (it
  // requires an authenticated session), so `forgotten` would always come back false here and this
  // handler would fall through to the stop+restart fallback - tearing the container down and
  // immediately starting a brand new one/login in the same click, an infinite-looking restart loop
  // for exactly the case an admin would use Logout to cancel. Cancelling the in-flight daemon login
  // instead (the same RPC the auth modal's own Cancel action uses) is accepted before authentication
  // completes on every daemon image, and never restarts the container.
  const handleLogoutPersistent = async (serviceKey: ScheduledPrefillServiceKey) => {
    const container = persistentContainerByService.get(getPersistentServiceId(serviceKey));
    if (!container) {
      return;
    }

    const serviceId = getPersistentServiceId(serviceKey);
    setPersistentAction({ serviceKey, action: 'logout' });
    setPersistentError(null);

    try {
      if (hasActivePersistentLogin(serviceId)) {
        // The pinned sessionId (RC3 fix, session 20260703-221336-2070027597) is the login flow's
        // OWN session, which may already differ from `container.sessionId` if a replacement
        // container has since started - falling back to the container's id only covers the edge
        // where cancel is clicked before the login ever pinned one (see usePersistentPrefillAuth's
        // cancel() for the matching case); cancel-login is idempotent on a mismatch either way.
        await ApiService.cancelPersistentLogin(
          serviceId,
          getPersistentLoginSessionId(serviceId) ?? container.sessionId
        );
        resetPersistentLoginState(serviceId);
        setPersistentLoginTarget((current) => (current === serviceKey ? null : current));
        await loadPersistentContainers();
        return;
      }

      const { forgotten } = await ApiService.logoutPersistentPrefillContainer(serviceId);
      if (forgotten) {
        resetPersistentLoginState(serviceId);
        setPersistentLoginTarget((current) => (current === serviceKey ? null : current));
        await loadPersistentContainers();
      } else {
        setPersistentLoginTarget((current) => (current === serviceKey ? null : current));
        await ApiService.stopPersistentPrefillContainer(container.sessionId);
        await runPersistentStartFlow(serviceKey);
        setPersistentError(t('prefill.persistent.messages.logoutFallbackNotice'));
      }
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
  };

  const handlePersistentLogin = (serviceKey: ScheduledPrefillServiceKey) => {
    const container = persistentContainerByService.get(getPersistentServiceId(serviceKey));
    if (!container?.isRunning) {
      setPersistentError(t(`${baseKey}.selectedGames.requiresPersistentContainer`));
      return;
    }

    setPersistentError(null);
    setPersistentLoginTarget(serviceKey);
    // Setting the target above is a same-value no-op for React whenever this service was already
    // the target (a dismissed-but-pending challenge, or a wedge where a prior start() settled with
    // nothing) - the mounted login component would never see the click. This nonce is watched by
    // its autostart effect independently of the target's value, so the click always reaches
    // beginLogin(), which itself decides resume-vs-fresh-start via state.hasChallenge.
    requestPersistentLoginAttempt(getPersistentServiceId(serviceKey));
  };

  const loadGameSelection = useCallback(
    async (serviceKey: ScheduledPrefillServiceKey, sessionId: string) => {
      setLoadingGameSelectionService(serviceKey);
      setGameSelectionError(null);

      try {
        // Persistent sessions are system-owned, so the user-scoped games route 403s. Use the
        // AdminOnly endpoint that resolves the running persistent session and bypasses ownership.
        const { games, cachedAppIds } = await ApiService.getPersistentPrefillGames(
          getPersistentServiceId(serviceKey)
        );

        const normalizedGames: ScheduledPrefillOwnedGame[] = games.map((game) => ({
          name: game.name,
          appId: String(game.appId)
        }));

        setGameSelection({
          serviceKey,
          sessionId,
          games: normalizedGames,
          cachedAppIds: cachedAppIds.map((appId) => String(appId))
        });
      } catch (error: unknown) {
        setGameSelectionError(getErrorMessage(error));
      } finally {
        setLoadingGameSelectionService(null);
      }
    },
    []
  );

  const handleOpenGameSelection = (serviceKey: ScheduledPrefillServiceKey) => {
    const serviceId = getPersistentServiceId(serviceKey);
    const isAnonymous = isScheduledPrefillAnonymousService(serviceKey);
    const container = persistentContainerByService.get(serviceId);
    if (!container?.isRunning) {
      setGameSelectionError(t(`${baseKey}.selectedGames.requiresPersistentContainer`));
      return;
    }

    // Select games never logs the user in on their behalf - only the explicit Log in button does
    // (diagnostic census #2). Surface the same needs-login hint the button's own disabled-state
    // tooltip already uses for this state (ScheduledPrefillPersistentCard's isGameSelectionBlocked).
    if (!isAnonymous && !container.isAuthenticated) {
      setGameSelectionError(t('prefill.persistent.loginToSelectGames'));
      return;
    }

    setGameSelectionError(null);
    setGameSelection({
      serviceKey,
      sessionId: container.sessionId,
      games: [],
      cachedAppIds: []
    });
    void loadGameSelection(serviceKey, container.sessionId);
  };

  const applyGameSelection = async (
    serviceKey: ScheduledPrefillServiceKey,
    selectedAppIds: string[]
  ) => {
    setConfig((current) =>
      current
        ? {
            ...current,
            [serviceKey]: {
              ...current[serviceKey],
              selectedAppIds
            }
          }
        : current
    );
    setValidationError(null);
    setSaveError(null);
    setGameSelectionError(null);

    const serviceId = getPersistentServiceId(serviceKey);
    const container = persistentContainerByService.get(serviceId);
    const isAnonymous = isScheduledPrefillAnonymousService(serviceKey);
    if (container?.isRunning && (isAnonymous || container.isAuthenticated)) {
      try {
        await ApiService.setPersistentPrefillSelectedApps(serviceId, selectedAppIds);
      } catch (error: unknown) {
        setGameSelectionError(getErrorMessage(error));
      }
    }
  };

  const handleSaveGameSelection = async (selectedIds: string[]) => {
    if (!gameSelection) {
      return;
    }

    const selectedAppIds = Array.from(new Set(selectedIds.map((selectedId) => String(selectedId))));
    await applyGameSelection(gameSelection.serviceKey, selectedAppIds);
  };

  const handleClearGameSelection = async (serviceKey: ScheduledPrefillServiceKey) => {
    await applyGameSelection(serviceKey, []);
  };

  const handlePersistentDownload = async (serviceKey: ScheduledPrefillServiceKey) => {
    if (!config) {
      return;
    }

    const serviceConfig = config[serviceKey];
    const serviceId = getPersistentServiceId(serviceKey);
    const container = persistentContainerByService.get(serviceId);
    const isAnonymous = isScheduledPrefillAnonymousService(serviceKey);

    if (!container?.isRunning || (!isAnonymous && !container.isAuthenticated)) {
      setPersistentError(t(`${baseKey}.persistentContainer.downloadRequiresAuth`));
      return;
    }

    if (serviceConfig.selectedAppIds.length === 0) {
      setPersistentError(t(`${baseKey}.persistentContainer.downloadRequiresSelection`));
      return;
    }

    setPersistentAction({ serviceKey, action: 'download' });
    setPersistentError(null);

    try {
      const maxConcurrency =
        serviceConfig.maxConcurrency.mode === 'Fixed' ? serviceConfig.maxConcurrency.value : null;

      await ApiService.startPersistentPrefill(serviceId, {
        appIds: serviceConfig.selectedAppIds,
        force: serviceConfig.force,
        operatingSystems: mapOperatingSystems(serviceConfig.operatingSystems),
        maxConcurrency
      });
      void loadPersistentContainers();
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
  };

  const handleCancelPersistentDownload = async (serviceKey: ScheduledPrefillServiceKey) => {
    const serviceId = getPersistentServiceId(serviceKey);
    setPersistentAction({ serviceKey, action: 'cancel' });
    setPersistentError(null);

    try {
      await ApiService.cancelPersistentPrefill(serviceId);
      void loadPersistentContainers();
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
  };

  const handleSave = async () => {
    if (!config) {
      return;
    }

    if (validationMessage) {
      setValidationError(validationMessage);
      return;
    }

    setSaving(true);
    setSaveError(null);
    setValidationError(null);

    try {
      // ONE save button for the whole modal: persist the schedule config and the
      // persistent-login validity together (the validity field no longer carries
      // its own Save).
      const nextValidityDays = clampToBounds(
        persistentValidityDays,
        PERSISTENT_PREFILL_VALIDITY_BOUNDS
      );
      await Promise.all([
        ApiService.updateScheduledPrefillConfig(config),
        ApiService.updatePersistentPrefillValidity({ days: nextValidityDays })
      ]);
      await Promise.resolve(onSaved?.());
      onClose();
    } catch (error: unknown) {
      setSaveError(getErrorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    if (!saving) {
      setGameSelection(null);
      onClose();
    }
  };

  return (
    <>
      <Modal
        opened={opened}
        onClose={handleClose}
        title={t(`${baseKey}.title`)}
        size="full"
        bodyFlexLayout
      >
        <div className="scheduled-prefill-config-modal">
          <div ref={setScrollAreaEl} className="scheduled-prefill-config-modal__scroll-area">
            <CustomScrollbar
              maxHeight={scrollAreaHeight != null ? `${scrollAreaHeight}px` : '100%'}
              radius="none"
            >
              <div className="scheduled-prefill-config-modal__scroll-content">
                {isLoading && !hasInitialData ? (
                  <div
                    className="scheduled-prefill-config-modal__skeleton"
                    role="status"
                    aria-busy="true"
                    aria-label={t(`${baseKey}.loading`)}
                  >
                    <div className="scheduled-prefill-config-modal__skeleton-overview" />
                    <div className="scheduled-prefill-config-modal__skeleton-body">
                      <div className="scheduled-prefill-config-modal__skeleton-nav">
                        {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => (
                          <div
                            key={serviceKey}
                            className="scheduled-prefill-config-modal__skeleton-nav-item"
                          />
                        ))}
                      </div>
                      <div className="scheduled-prefill-config-modal__skeleton-detail">
                        <div className="scheduled-prefill-config-modal__skeleton-block" />
                        <div className="scheduled-prefill-config-modal__skeleton-block" />
                        <div className="scheduled-prefill-config-modal__skeleton-block scheduled-prefill-config-modal__skeleton-block--tall" />
                      </div>
                    </div>
                  </div>
                ) : (
                  <>
                    <div className="scheduled-prefill-config-modal__overview">
                      <div className="scheduled-prefill-config-modal__overview-main">
                        <p className="scheduled-prefill-config-modal__overview-description">
                          {t(`${baseKey}.modalDescription`)}
                        </p>
                        <div className="scheduled-prefill-config-modal__overview-status">
                          <Badge variant="info">
                            {t(`${baseKey}.summary`, {
                              enabled: enabledCount,
                              total: SCHEDULED_PREFILL_SERVICE_RUN_ORDER.length
                            })}
                          </Badge>
                          <HelpPopover position="left" width={360} maxHeight="20rem">
                            <ul className="scheduled-prefill-config-modal__help-list">
                              <li className="schedule-extra-help">
                                {t(`${baseKey}.auth.authPathsBattleNet`)}
                              </li>
                              <li className="schedule-extra-help">
                                {t(`${baseKey}.auth.authPathsRiot`)}
                              </li>
                              <li className="schedule-extra-help">
                                {t(`${baseKey}.auth.authPathsPersistent`)}
                              </li>
                            </ul>
                          </HelpPopover>
                        </div>
                      </div>

                      {hasPersistentLoginWarning && (
                        <p className="scheduled-prefill-config-modal__overview-warning">
                          {t(`${baseKey}.authWarning`)}
                        </p>
                      )}

                      <div className="scheduled-prefill-config-modal__settings-list">
                        <div className="scheduled-prefill-config-modal__setting-row">
                          <div className="scheduled-prefill-config-modal__setting-copy">
                            <span className="scheduled-prefill-config-modal__global-label">
                              {t(`${baseKey}.bulkToggle.label`)}
                            </span>
                            <p className="scheduled-prefill-config-modal__global-help">
                              {t(`${baseKey}.bulkToggle.help`)}
                            </p>
                          </div>
                          <div className="scheduled-prefill-config-modal__setting-actions">
                            <Button
                              type="button"
                              variant="default"
                              size={SCHEDULED_PREFILL_BUTTON_SIZE}
                              onClick={() => handleSetAllServicesEnabled(true)}
                              disabled={
                                !config ||
                                saving ||
                                loadingConfig ||
                                enabledCount === SCHEDULED_PREFILL_SERVICE_RUN_ORDER.length
                              }
                            >
                              {t(`${baseKey}.bulkToggle.enableAll`)}
                            </Button>
                            <Button
                              type="button"
                              variant="default"
                              size={SCHEDULED_PREFILL_BUTTON_SIZE}
                              onClick={() => handleSetAllServicesEnabled(false)}
                              disabled={!config || saving || loadingConfig || enabledCount === 0}
                            >
                              {t(`${baseKey}.bulkToggle.disableAll`)}
                            </Button>
                          </div>
                        </div>

                        <div className="scheduled-prefill-config-modal__setting-row">
                          <div className="scheduled-prefill-config-modal__setting-copy">
                            <label
                              className="scheduled-prefill-config-modal__global-label"
                              htmlFor="scheduled-prefill-persistent-validity-days"
                            >
                              {t(`${baseKey}.settings.persistentValidityLabel`)}
                            </label>
                            <p className="scheduled-prefill-config-modal__global-help">
                              {t(`${baseKey}.settings.persistentValidityHelp`, {
                                min: PERSISTENT_PREFILL_VALIDITY_BOUNDS.min,
                                max: PERSISTENT_PREFILL_VALIDITY_BOUNDS.max
                              })}
                            </p>
                          </div>
                          <div className="scheduled-prefill-config-modal__setting-actions">
                            {/* Saved by the modal's single Save button - no inline save here. */}
                            <NumberInput
                              id="scheduled-prefill-persistent-validity-days"
                              className="scheduled-prefill-number-cap scheduled-prefill-number-cap--full"
                              min={PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                              max={PERSISTENT_PREFILL_VALIDITY_BOUNDS.max}
                              step={1}
                              value={persistentValidityDays}
                              disabled={loadingGlobalSettings || saving}
                              aria-label={t(`${baseKey}.settings.persistentValidityLabel`)}
                              onChange={handlePersistentValidityDaysChange}
                            />
                          </div>
                        </div>
                      </div>

                      <div className="scheduled-prefill-config-modal__danger-zone">
                        <div className="scheduled-prefill-config-modal__danger-zone-copy">
                          <p className="scheduled-prefill-config-modal__danger-zone-title">
                            {t(`${baseKey}.settings.clearLogins.zoneTitle`)}
                          </p>
                          <p className="scheduled-prefill-config-modal__danger-zone-help">
                            {t(`${baseKey}.settings.clearLogins.help`)}
                          </p>
                        </div>
                        <div className="scheduled-prefill-config-modal__danger-zone-action">
                          <Button
                            type="button"
                            variant="filled"
                            color="red"
                            size={SCHEDULED_PREFILL_BUTTON_SIZE}
                            onClick={() => setClearLoginsConfirmOpen(true)}
                            disabled={clearingLogins}
                            loading={clearingLogins}
                          >
                            {clearingLogins
                              ? t(`${baseKey}.settings.clearLogins.clearing`)
                              : t(`${baseKey}.settings.clearLogins.button`)}
                          </Button>
                        </div>
                      </div>
                      {banner && (
                        <Alert
                          color={banner.color}
                          className="scheduled-prefill-config-modal__alert"
                          withCloseButton
                          onClose={dismissBanner}
                        >
                          {banner.message}
                        </Alert>
                      )}
                    </div>

                    {config ? (
                      <ScheduledPrefillPlatformsPanel
                        config={config}
                        disabled={saving || loadingConfig}
                        statusLoading={isInitialPersistentContainersLoad}
                        containersByServiceKey={containersByServiceKey}
                        selectedGamesCountByServiceKey={selectedGamesCountByServiceKey}
                        persistentAction={persistentAction}
                        authenticatingServiceKeys={authenticatingServiceKeys}
                        gameSelectionLoadingServiceKey={loadingGameSelectionService}
                        onServiceChange={handleServiceChange}
                        onStart={(serviceKey) => void handleStartPersistent(serviceKey)}
                        onStop={(serviceKey) => void handleStopPersistent(serviceKey)}
                        onLogin={handlePersistentLogin}
                        onLogout={(serviceKey) => void handleLogoutPersistent(serviceKey)}
                        onSelectGames={(serviceKey) => void handleOpenGameSelection(serviceKey)}
                        onClearGames={(serviceKey) => void handleClearGameSelection(serviceKey)}
                        onDownload={(serviceKey) => void handlePersistentDownload(serviceKey)}
                        onCancelDownload={(serviceKey) =>
                          void handleCancelPersistentDownload(serviceKey)
                        }
                      />
                    ) : (
                      <div className="scheduled-prefill-config-modal__empty">
                        {t(`${baseKey}.empty`)}
                      </div>
                    )}
                  </>
                )}
              </div>
            </CustomScrollbar>
          </div>

          <div className="scheduled-prefill-config-modal__actions">
            <Button
              type="button"
              variant="default"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={handleClose}
              disabled={saving}
            >
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="filled"
              color="green"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={handleSave}
              disabled={!config || saving || loadingConfig}
              loading={saving}
            >
              {saving ? t(`${baseKey}.actions.saving`) : t(`${baseKey}.actions.save`)}
            </Button>
          </div>
        </div>
      </Modal>
      {persistentLoginTarget && (
        <PersistentLoginHost
          serviceKey={persistentLoginTarget}
          isRunning={
            persistentContainerByService.get(getPersistentServiceId(persistentLoginTarget))
              ?.isRunning ?? false
          }
          isAuthenticated={
            persistentContainerByService.get(getPersistentServiceId(persistentLoginTarget))
              ?.isAuthenticated ?? false
          }
          onAuthenticated={() => {
            void loadPersistentContainers();
          }}
          onDismiss={() => {
            setPersistentLoginTarget(null);
          }}
        />
      )}
      <GameSelectionModal
        opened={gameSelection !== null}
        onClose={() => setGameSelection(null)}
        games={gameSelection?.games ?? []}
        selectedAppIds={
          gameSelection && config ? config[gameSelection.serviceKey].selectedAppIds : []
        }
        onSave={handleSaveGameSelection}
        isLoading={loadingGameSelectionService !== null}
        cachedAppIds={gameSelection?.cachedAppIds ?? []}
      />
      <ConfirmationModal
        opened={clearLoginsConfirmOpen}
        onClose={() => setClearLoginsConfirmOpen(false)}
        onConfirm={() => void handleConfirmClearLogins()}
        title={t(`${baseKey}.settings.clearLogins.confirmTitle`)}
        confirmLabel={t(`${baseKey}.settings.clearLogins.confirmButton`)}
        confirmColor="red"
        loading={clearingLogins}
      >
        <p className="text-sm text-themed-muted">
          {t(`${baseKey}.settings.clearLogins.confirmBody`)}
        </p>
      </ConfirmationModal>
    </>
  );
}
