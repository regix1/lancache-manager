import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import ApiService from '@services/api.service';
import { useAuth } from '@contexts/useAuth';
import { useSteamAuth } from '@contexts/useSteamAuth';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { sessionStore } from '@utils/storage';
import { getErrorMessage, isAbortError } from '@utils/error';
import {
  isPrefillRunActive,
  mergePrefillRuns
} from '@components/features/prefill/hooks/prefillTypes';
import { PERSISTENT_PREFILL_SERVICES } from '@components/features/prefill/persistentPrefillConstants';
import type {
  PersistentIntegrationLoginAvailability,
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER,
  SCHEDULED_PREFILL_TRANSIENT_STOP_GRACE_MS
} from './constants';
import {
  getPersistentServiceId,
  isScheduledPrefillAccountService
} from './scheduledPrefillPlatformUi';
import { usePersistentPrefillContainerSignalR } from './usePersistentPrefillContainerSignalR';
import { usePersistentLoginChallengeSignalR } from './usePersistentLoginChallengeSignalR';
import {
  endPersistentLogin,
  getPersistentLoginStartRequest,
  getPersistentLoginState,
  hasActivePersistentLogin,
  isPersistentLoginIntegrationReuse,
  isPersistentLoginDismissed,
  markPersistentLoginAuthenticated,
  reconcilePersistentLoginFromServer,
  requestPersistentLoginAttempt,
  resetPersistentLoginState,
  setPersistentLoginStartSessionId,
  updatePersistentLoginState,
  usePersistentLoginStoreVersion
} from './persistentLoginStore';
import { recoverScheduledPrefillEditSession } from './scheduledPrefillEditSessionLedger';
import { normalizePersistentLoginClearResults } from './persistentLoginClearResult';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import type { ScheduledPrefillServiceKey } from './types';
import { getIntegrationReasonKey } from '../../../../../types';

export function useScheduledPrefillContainers(activeService: ScheduledPrefillServiceKey | null) {
  const { t } = useTranslation();
  const {
    accountId,
    authMode,
    sessionId,
    authenticationEnabled,
    isLoading: authLoading
  } = useAuth();
  const { revision } = useSteamAuth();
  const { isConnected } = useSignalR();

  const opened = true;
  const [persistentContainers, setPersistentContainers] = useState<
    PersistentPrefillContainerDto[] | null
  >(null);
  const persistentContainersRef = useRef<PersistentPrefillContainerDto[] | null>(null);
  persistentContainersRef.current = persistentContainers;
  const [loadingPersistentContainers, setLoadingPersistentContainers] = useState(false);
  const [integrationLoginAvailabilityByService, setIntegrationLoginAvailabilityByService] =
    useState<Map<ScheduledPrefillServiceKey, PersistentIntegrationLoginAvailability>>(new Map());
  const [integrationLoginAvailabilityIdentity, setIntegrationLoginAvailabilityIdentity] =
    useState('');
  const integrationLoginAvailabilityIdentityRef = useRef('');
  integrationLoginAvailabilityIdentityRef.current = integrationLoginAvailabilityIdentity;
  const [loadingIntegrationLoginAvailability, setLoadingIntegrationLoginAvailability] =
    useState(false);
  const [integrationLoginErrors, setIntegrationLoginErrors] = useState<
    Partial<Record<ScheduledPrefillServiceKey, string>>
  >({});
  const [integrationLoginErrorsIdentity, setIntegrationLoginErrorsIdentity] = useState('');
  const integrationLoginErrorsIdentityRef = useRef('');
  integrationLoginErrorsIdentityRef.current = integrationLoginErrorsIdentity;
  const [persistentError, setPersistentError] = useState<string | null>(null);
  const [actions, setActions] = useState<
    Partial<Record<ScheduledPrefillServiceKey, ScheduledPrefillPersistentActionState['action']>>
  >({});
  const [cancellingRunIds, setCancellingRunIds] = useState<string[]>([]);
  const cancellingRunsRef = useRef(new Set<string>());
  const [runErrors, setRunErrors] = useState<Record<string, string>>({});

  const [persistentLoginTarget, setPersistentLoginTarget] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const login = useRef<{
    serviceKey: ScheduledPrefillServiceKey;
    sessionId: string;
    identity: string;
    visible: boolean;
  } | null>(null);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const privateAvailabilityIdentity = `${authenticationEnabled}:${authMode}:${accountId ?? ''}:${sessionId ?? ''}`;
  const privateAvailabilityIdentityRef = useRef(privateAvailabilityIdentity);
  privateAvailabilityIdentityRef.current = privateAvailabilityIdentity;
  const canUseSavedLogin =
    !authLoading &&
    (authenticationEnabled === false ||
      (authMode === 'authenticated' && Boolean(accountId && sessionId)));
  const requiresIndividualAccount =
    authenticationEnabled !== false && authMode === 'authenticated' && !accountId;

  const persistentContainersRequestRef = useRef<{
    controller: AbortController;
    again: boolean;
    promise: Promise<void>;
  } | null>(null);
  const persistentContainersRevisionRef = useRef(0);

  const loadPersistentContainers = useCallback(async (signal?: AbortSignal) => {
    if (signal?.aborted) return;
    persistentContainersRevisionRef.current += 1;
    if (
      persistentContainersRequestRef.current &&
      !persistentContainersRequestRef.current.controller.signal.aborted
    ) {
      persistentContainersRequestRef.current.again = true;
      return persistentContainersRequestRef.current.promise;
    }

    const controller = new AbortController();
    const abort = () => controller.abort();
    signal?.addEventListener('abort', abort, { once: true });
    const request = { controller, again: false, promise: Promise.resolve() };
    persistentContainersRequestRef.current = request;
    setLoadingPersistentContainers(persistentContainersRef.current === null);
    request.promise = (async () => {
      try {
        do {
          request.again = false;
          const revision = persistentContainersRevisionRef.current;
          try {
            const nextContainers = await ApiService.getPersistentPrefillContainers(
              controller.signal
            );
            if (controller.signal.aborted || persistentContainersRequestRef.current !== request)
              return;
            if (revision !== persistentContainersRevisionRef.current) continue;
            setPersistentContainers((current) => {
              const next = nextContainers.map((container) => {
                const previous = current?.find((item) => item.sessionId === container.sessionId);
                return previous?.runs && container.runs
                  ? { ...container, runs: mergePrefillRuns(previous.runs, container.runs) }
                  : container;
              });
              persistentContainersRef.current = next;
              return next;
            });
            setPersistentError(null);
          } catch (error: unknown) {
            if (
              !controller.signal.aborted &&
              persistentContainersRequestRef.current === request &&
              revision === persistentContainersRevisionRef.current &&
              !isAbortError(error)
            ) {
              setPersistentError(getErrorMessage(error));
            }
          }
        } while (request.again && !controller.signal.aborted);
      } finally {
        signal?.removeEventListener('abort', abort);
        if (persistentContainersRequestRef.current === request) {
          persistentContainersRequestRef.current = null;
          setLoadingPersistentContainers(false);
        }
      }
    })();

    return request.promise;
  }, []);

  const integrationLoginRequestRef = useRef<AbortController | null>(null);
  const loadIntegrationLoginAvailability = useCallback(
    async (signal?: AbortSignal) => {
      if (signal?.aborted) return;
      integrationLoginRequestRef.current?.abort();
      const controller = new AbortController();
      integrationLoginRequestRef.current = controller;
      const requestIdentity = privateAvailabilityIdentityRef.current;
      const isCurrent = () =>
        integrationLoginRequestRef.current === controller &&
        !controller.signal.aborted &&
        requestIdentity === privateAvailabilityIdentityRef.current;
      if (!canUseSavedLogin) {
        if (isCurrent()) {
          setIntegrationLoginAvailabilityByService(
            new Map(
              requiresIndividualAccount
                ? SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map((serviceKey) => [
                    serviceKey,
                    { available: false, account: null, reason: 'account-required' }
                  ])
                : []
            )
          );
          setIntegrationLoginAvailabilityIdentity(requestIdentity);
          setIntegrationLoginErrors({});
          setIntegrationLoginErrorsIdentity(requestIdentity);
          setLoadingIntegrationLoginAvailability(false);
        }
        return;
      }

      const abort = () => controller.abort();
      signal?.addEventListener('abort', abort, { once: true });
      if (isCurrent())
        setLoadingIntegrationLoginAvailability(
          integrationLoginAvailabilityIdentityRef.current !== requestIdentity
        );
      try {
        const availability = await Promise.all(
          SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map(async (serviceKey) => {
            try {
              const result = await ApiService.getPersistentIntegrationLoginAvailability(
                getPersistentServiceId(serviceKey),
                controller.signal
              );
              return { serviceKey, result } as const;
            } catch (error: unknown) {
              if (isAbortError(error)) {
                throw error;
              }
              return { serviceKey, error: getErrorMessage(error) } as const;
            }
          })
        );
        if (isCurrent()) {
          setIntegrationLoginAvailabilityByService((current) => {
            const next =
              integrationLoginAvailabilityIdentityRef.current === requestIdentity
                ? new Map(current)
                : new Map<ScheduledPrefillServiceKey, PersistentIntegrationLoginAvailability>();
            for (const entry of availability) {
              if ('result' in entry && entry.result !== undefined)
                next.set(entry.serviceKey, entry.result);
            }
            return next;
          });
          setIntegrationLoginErrors((current) => {
            const next =
              integrationLoginErrorsIdentityRef.current === requestIdentity ? { ...current } : {};
            for (const entry of availability) {
              if ('error' in entry) next[entry.serviceKey] = entry.error;
              else delete next[entry.serviceKey];
            }
            return next;
          });
          setIntegrationLoginAvailabilityIdentity(requestIdentity);
          setIntegrationLoginErrorsIdentity(requestIdentity);
        }
      } catch (error: unknown) {
        if (!isAbortError(error) && isCurrent()) {
          setIntegrationLoginErrors(
            Object.fromEntries(
              SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.map((serviceKey) => [
                serviceKey,
                getErrorMessage(error)
              ])
            )
          );
          setIntegrationLoginAvailabilityIdentity(requestIdentity);
          setIntegrationLoginErrorsIdentity(requestIdentity);
        }
      } finally {
        signal?.removeEventListener('abort', abort);
        if (isCurrent()) {
          setLoadingIntegrationLoginAvailability(false);
        }
      }
    },
    [canUseSavedLogin, requiresIndividualAccount]
  );

  const visibleIntegrationLoginErrors = useMemo(
    () =>
      integrationLoginErrorsIdentity === privateAvailabilityIdentity ? integrationLoginErrors : {},
    [integrationLoginErrors, integrationLoginErrorsIdentity, privateAvailabilityIdentity]
  );
  const visibleIntegrationLoginAvailabilityByService = useMemo(() => {
    if (integrationLoginAvailabilityIdentity !== privateAvailabilityIdentity)
      return new Map<ScheduledPrefillServiceKey, PersistentIntegrationLoginAvailability>();
    return new Map(
      [...integrationLoginAvailabilityByService].filter(
        ([serviceKey]) => visibleIntegrationLoginErrors[serviceKey] === undefined
      )
    );
  }, [
    integrationLoginAvailabilityByService,
    integrationLoginAvailabilityIdentity,
    privateAvailabilityIdentity,
    visibleIntegrationLoginErrors
  ]);

  const privateAvailabilityIdentityAppliedRef = useRef(privateAvailabilityIdentity);
  useEffect(() => {
    if (privateAvailabilityIdentityAppliedRef.current === privateAvailabilityIdentity) {
      return;
    }

    privateAvailabilityIdentityAppliedRef.current = privateAvailabilityIdentity;
    const admitted = login.current;
    if (admitted && admitted.identity !== privateAvailabilityIdentity) {
      admitted.visible = false;
      const serviceId = getPersistentServiceId(admitted.serviceKey);
      const storeSession =
        getPersistentLoginState(serviceId).sessionId ??
        getPersistentLoginStartRequest(serviceId)?.sessionId;
      if (storeSession === admitted.sessionId) resetPersistentLoginState(serviceId);
      login.current = null;
      setPersistentLoginTarget((current) => (current === admitted.serviceKey ? null : current));
    }
    const privateReuseServiceKeys = SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.filter((serviceKey) =>
      isPersistentLoginIntegrationReuse(getPersistentServiceId(serviceKey))
    );
    for (const serviceKey of privateReuseServiceKeys) {
      resetPersistentLoginState(getPersistentServiceId(serviceKey));
    }
    setPersistentLoginTarget((current) =>
      current &&
      isScheduledPrefillAccountService(current) &&
      privateReuseServiceKeys.includes(current)
        ? null
        : current
    );
  }, [privateAvailabilityIdentity]);

  useEffect(() => {
    if (!opened) {
      integrationLoginRequestRef.current?.abort();
      return;
    }

    const controller = new AbortController();
    void loadIntegrationLoginAvailability(controller.signal);
    return () => {
      controller.abort();
      integrationLoginRequestRef.current?.abort();
    };
  }, [opened, privateAvailabilityIdentity, revision, loadIntegrationLoginAvailability]);

  const persistentContainerByService = useMemo(
    () =>
      new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
        (persistentContainers ?? []).map((container) => [container.service, container])
      ),
    [persistentContainers]
  );

  const persistentContainerByServiceRef = useRef(persistentContainerByService);
  persistentContainerByServiceRef.current = persistentContainerByService;

  const persistentReconcileSignature = useMemo(
    () =>
      (persistentContainers ?? [])
        .map(
          (container) =>
            `${container.service}:${container.isRunning ? 1 : 0}:${
              container.isAuthenticated ? 1 : 0
            }:${container.sessionId}`
        )
        .join('|'),
    [persistentContainers]
  );

  useEffect(() => {
    const admitted = login.current;
    if (
      !opened ||
      admitted === null ||
      !admitted.visible ||
      persistentLoginTarget !== admitted.serviceKey ||
      activeService !== admitted.serviceKey ||
      admitted.identity !== privateAvailabilityIdentityRef.current
    ) {
      return;
    }

    const serviceId = getPersistentServiceId(admitted.serviceKey);
    const container = persistentContainerByServiceRef.current.get(serviceId);
    if (
      !container?.isRunning ||
      container.isAuthenticated ||
      container.sessionId !== admitted.sessionId ||
      hasActivePersistentLogin(serviceId) ||
      isPersistentLoginDismissed(serviceId)
    ) {
      return;
    }

    const controller = new AbortController();

    const reconcile = async () => {
      const result = await reconcilePersistentLoginFromServer(serviceId, admitted.sessionId, {
        noResult: t('prefill.persistent.errors.noResult'),
        timedOut: t('prefill.persistent.loginTimedOut')
      });
      if (
        controller.signal.aborted ||
        login.current !== admitted ||
        !admitted.visible ||
        admitted.identity !== privateAvailabilityIdentityRef.current ||
        activeRef.current !== admitted.serviceKey ||
        persistentContainerByServiceRef.current.get(serviceId)?.sessionId !== admitted.sessionId
      ) {
        return;
      }

      if (
        (result === 'challenge' || result === 'unavailable') &&
        !isPersistentLoginDismissed(serviceId)
      ) {
        setPersistentLoginTarget(admitted.serviceKey);
      }
    };

    void reconcile();

    return () => {
      controller.abort();
    };
  }, [opened, persistentLoginTarget, persistentReconcileSignature, activeService, t]);

  const containersByServiceKey = useMemo(
    () =>
      new Map(
        SCHEDULED_PREFILL_SERVICE_RUN_ORDER.flatMap((serviceKey) => {
          const container = persistentContainerByService.get(getPersistentServiceId(serviceKey));
          return container ? [[serviceKey, container] as const] : [];
        })
      ),
    [persistentContainerByService]
  );
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
      serviceId: PersistentPrefillServiceId,
      authenticated = false
    ) => {
      const admitted = login.current;
      if (admitted?.serviceKey === serviceKey) {
        admitted.visible = false;
        login.current = null;
      }
      if (authenticated) markPersistentLoginAuthenticated(serviceId);
      else resetPersistentLoginState(serviceId);
      setPersistentLoginTarget((current) => (current === serviceKey ? null : current));
    };

    for (const service of PERSISTENT_PREFILL_SERVICES) {
      const container = persistentContainerByService.get(service.service);
      const admitted = login.current;
      if (
        admitted?.serviceKey === service.key &&
        container?.isRunning &&
        container.sessionId !== admitted.sessionId
      ) {
        clearPendingTimer(service.service);
        retire(service.key, service.service);
        continue;
      }
      const authenticatedElsewhere = container?.isRunning && container.isAuthenticated;
      const stoppedOrMissing = !container?.isRunning;

      if (!authenticatedElsewhere && !stoppedOrMissing) {
        clearPendingTimer(service.service);
        continue;
      }

      if (authenticatedElsewhere) {
        clearPendingTimer(service.service);
        const storeSession =
          getPersistentLoginState(service.service).sessionId ??
          getPersistentLoginStartRequest(service.service)?.sessionId;
        retire(service.key, service.service, storeSession === container.sessionId);
        continue;
      }

      if (!hasActivePersistentLogin(service.service)) {
        clearPendingTimer(service.service);
        retire(service.key, service.service);
        continue;
      }

      if (!timers.has(service.service)) {
        const pendingAdmission =
          login.current?.serviceKey === service.key ? login.current : undefined;
        const timer = setTimeout(() => {
          timers.delete(service.service);
          if (pendingAdmission && login.current !== pendingAdmission) return;
          if (persistentContainerByServiceRef.current.get(service.service)?.isRunning) return;
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

  usePersistentPrefillContainerSignalR({
    enabled: opened,
    onRefresh: () => {
      void loadPersistentContainers();
    }
  });

  usePersistentLoginChallengeSignalR({
    enabled: shouldWatchPersistentAuth,
    containersByService: persistentContainerByService
  });

  usePersistentLoginStoreVersion();
  const authenticatingServiceKeys = SCHEDULED_PREFILL_ACCOUNT_SERVICE_IDS.filter((serviceKey) => {
    const serviceId = getPersistentServiceId(serviceKey);
    const container = persistentContainerByService.get(serviceId);
    const admitted = login.current;
    return (
      container?.isRunning &&
      !container.isAuthenticated &&
      admitted?.serviceKey === serviceKey &&
      admitted.sessionId === container.sessionId &&
      admitted.identity === privateAvailabilityIdentity &&
      hasActivePersistentLogin(serviceId) &&
      !isPersistentLoginDismissed(serviceId)
    );
  });

  const [errors, setErrors] = useState<Partial<Record<ScheduledPrefillServiceKey, string>>>({});
  // Which action wrote each entry in `errors`, so a Services row shows only its own Start failure.
  const [errorActions, setErrorActions] = useState<
    Partial<Record<ScheduledPrefillServiceKey, ScheduledPrefillPersistentActionState['action']>>
  >({});
  // A running container means someone started it after this tab's Start failed, so that failure
  // no longer describes the row and must not return when the container later stops [88]
  useEffect(() => {
    const stale = SCHEDULED_PREFILL_SERVICE_RUN_ORDER.filter(
      (serviceKey) =>
        errorActions[serviceKey] === 'start' && containersByServiceKey.get(serviceKey)?.isRunning
    );
    if (stale.length === 0) return;
    const cleared = Object.fromEntries(stale.map((serviceKey) => [serviceKey, undefined]));
    setErrors((previous) => ({ ...previous, ...cleared }));
    setErrorActions((previous) => ({ ...previous, ...cleared }));
  }, [containersByServiceKey, errorActions]);
  const attempts = useRef(new Map<ScheduledPrefillServiceKey, object>());
  const activeRef = useRef(activeService);
  activeRef.current = activeService;
  const view = useRef({ service: activeService });
  if (view.current.service !== activeService) view.current = { service: activeService };
  const recover = useCallback(
    () =>
      recoverScheduledPrefillEditSession(sessionStore, (request) =>
        ApiService.cleanupPersistentPrefillEditSession(request)
      ),
    []
  );
  useEffect(() => {
    const controller = new AbortController();
    void loadPersistentContainers(controller.signal);
    return () => {
      persistentContainersRevisionRef.current += 1;
      controller.abort();
      persistentContainersRequestRef.current?.controller.abort();
      if (login.current) login.current.visible = false;
      login.current = null;
    };
  }, [loadPersistentContainers]);
  useReconnectRefetch(isConnected, () => {
    void loadPersistentContainers();
    void loadIntegrationLoginAvailability();
  });
  const act = async (
    serviceKey: ScheduledPrefillServiceKey,
    action: ScheduledPrefillPersistentActionState['action'],
    run: (current: () => boolean) => Promise<void>
  ) => {
    if (privateAvailabilityIdentity !== privateAvailabilityIdentityRef.current) return;
    if (attempts.current.has(serviceKey)) return;
    const attempt = {};
    const identity = privateAvailabilityIdentity;
    const opening = view.current;
    const session = persistentContainerByServiceRef.current.get(
      getPersistentServiceId(serviceKey)
    )?.sessionId;
    attempts.current.set(serviceKey, attempt);
    const current = () =>
      attempts.current.get(serviceKey) === attempt &&
      identity === privateAvailabilityIdentityRef.current &&
      (action !== 'login' || view.current === opening) &&
      (action === 'start' ||
        session ===
          persistentContainerByServiceRef.current.get(getPersistentServiceId(serviceKey))
            ?.sessionId);
    setActions((value) => ({ ...value, [serviceKey]: action }));
    setErrors((previous) => ({ ...previous, [serviceKey]: undefined }));
    setErrorActions((previous) => ({ ...previous, [serviceKey]: undefined }));
    try {
      await recover();
      if (!current()) return;
      await run(current);
      await loadPersistentContainers();
    } catch (error: unknown) {
      if (current()) {
        setErrors((previous) => ({ ...previous, [serviceKey]: getErrorMessage(error) }));
        setErrorActions((previous) => ({ ...previous, [serviceKey]: action }));
      }
    } finally {
      if (attempts.current.get(serviceKey) === attempt) {
        attempts.current.delete(serviceKey);
        setActions((value) => ({ ...value, [serviceKey]: undefined }));
      }
    }
  };
  const handleStartPersistent = (serviceKey: ScheduledPrefillServiceKey) =>
    act(serviceKey, 'start', async () => {
      const serviceId = getPersistentServiceId(serviceKey);
      resetPersistentLoginState(serviceId);
      await ApiService.startPersistentPrefillContainer(serviceId);
    });
  const handleStopPersistent = (serviceKey: ScheduledPrefillServiceKey) => {
    const container = containersByServiceKey.get(serviceKey);
    if (!container) return Promise.resolve();
    return act(serviceKey, 'stop', async (current) => {
      await ApiService.stopPersistentPrefillContainer(container.sessionId);
      if (
        current() &&
        persistentContainerByServiceRef.current.get(container.service)?.sessionId ===
          container.sessionId
      ) {
        if (login.current?.serviceKey === serviceKey) {
          login.current.visible = false;
          login.current = null;
        }
        resetPersistentLoginState(container.service);
        setPersistentLoginTarget((value) => (value === serviceKey ? null : value));
      }
    });
  };
  const handleLogoutPersistent = (serviceKey: ScheduledPrefillServiceKey) => {
    const container = containersByServiceKey.get(serviceKey);
    if (!container) return Promise.resolve();
    return act(serviceKey, 'logout', async (current) => {
      const serviceId = getPersistentServiceId(serviceKey);
      if (hasActivePersistentLogin(serviceId)) {
        const cancelled = await endPersistentLogin(serviceId, container.sessionId);
        if (!cancelled) throw new Error(t('prefill.persistent.cancelLoginFailed'));
      } else {
        const { forgotten } = await ApiService.logoutPersistentPrefillContainer(
          serviceId,
          container.sessionId
        );
        if (!forgotten) {
          throw new Error(t('management.auth.errors.logoutFailed'));
        }
      }
      if (current()) {
        if (login.current?.serviceKey === serviceKey) {
          login.current.visible = false;
          login.current = null;
        }
        resetPersistentLoginState(serviceId);
        setPersistentLoginTarget((value) => (value === serviceKey ? null : value));
      }
    });
  };
  const handlePersistentLogin = (
    serviceKey: ScheduledPrefillServiceKey,
    reuseIntegration: boolean
  ) =>
    act(serviceKey, 'login', async (current) => {
      const serviceId = getPersistentServiceId(serviceKey);
      const container = persistentContainerByServiceRef.current.get(serviceId);
      const availability = visibleIntegrationLoginAvailabilityByService.get(serviceKey);
      if (!current() || activeRef.current !== serviceKey) return;
      if (
        reuseIntegration &&
        (!canUseSavedLogin ||
          availability?.available !== true ||
          visibleIntegrationLoginErrors[serviceKey] !== undefined)
      ) {
        throw new Error(
          availability?.available === false
            ? t(getIntegrationReasonKey(availability.reason))
            : t('errors.integration.statusUnavailable')
        );
      }
      if (!container?.isRunning)
        throw new Error(t(`${baseKey}.selectedGames.requiresPersistentContainer`));
      const previous = login.current;
      const sameAdmission =
        previous?.serviceKey === serviceKey &&
        previous.sessionId === container.sessionId &&
        previous.identity === privateAvailabilityIdentity;
      const ownedAttempt =
        sameAdmission &&
        (hasActivePersistentLogin(serviceId) ||
          getPersistentLoginStartRequest(serviceId) !== undefined);
      if (previous && !ownedAttempt) {
        previous.visible = false;
        const previousServiceId = getPersistentServiceId(previous.serviceKey);
        const previousSession =
          getPersistentLoginState(previousServiceId).sessionId ??
          getPersistentLoginStartRequest(previousServiceId)?.sessionId;
        if (previousSession === previous.sessionId) resetPersistentLoginState(previousServiceId);
      }
      if (
        !ownedAttempt &&
        (hasActivePersistentLogin(serviceId) || getPersistentLoginStartRequest(serviceId))
      ) {
        resetPersistentLoginState(serviceId);
      }
      const admitted = {
        serviceKey,
        sessionId: container.sessionId,
        identity: privateAvailabilityIdentity,
        visible: true
      };
      login.current = admitted;
      if (!hasActivePersistentLogin(serviceId)) {
        setPersistentLoginStartSessionId(
          serviceId,
          container.sessionId,
          undefined,
          undefined,
          reuseIntegration
        );
      }
      setPersistentLoginTarget(serviceKey);
      requestPersistentLoginAttempt(serviceId);
    });

  const renderedLogin = login.current;
  const handleDismissPersistentLogin = useCallback(() => {
    const admitted = renderedLogin;
    if (
      admitted === null ||
      login.current !== admitted ||
      admitted.identity !== privateAvailabilityIdentityRef.current ||
      persistentContainerByServiceRef.current.get(getPersistentServiceId(admitted.serviceKey))
        ?.sessionId !== admitted.sessionId
    ) {
      return;
    }

    admitted.visible = false;
    updatePersistentLoginState(getPersistentServiceId(admitted.serviceKey), (current) => ({
      ...current,
      dismissed: true
    }));
    setPersistentLoginTarget((current) => (current === admitted.serviceKey ? null : current));
  }, [renderedLogin]);

  const visiblePersistentLoginTarget = (() => {
    const admitted = login.current;
    if (
      persistentLoginTarget === null ||
      admitted === null ||
      !admitted.visible ||
      admitted.serviceKey !== persistentLoginTarget ||
      admitted.identity !== privateAvailabilityIdentity ||
      persistentContainerByService.get(getPersistentServiceId(admitted.serviceKey))?.sessionId !==
        admitted.sessionId
    ) {
      return null;
    }
    return persistentLoginTarget;
  })();
  const handleCancelPersistentDownload = async (
    serviceKey: ScheduledPrefillServiceKey,
    runId?: string
  ) => {
    const serviceId = getPersistentServiceId(serviceKey);
    const container = persistentContainerByService.get(serviceId);
    const targetId = runId ?? container?.runId;
    const run = container?.runs?.find((item) => item.runId === targetId);
    if (
      !container?.isRunning ||
      !targetId ||
      cancellingRunsRef.current.has(targetId) ||
      (run && (!isPrefillRunActive(run) || run.cancelRequested))
    ) {
      return;
    }
    cancellingRunsRef.current.add(targetId);
    setCancellingRunIds([...cancellingRunsRef.current]);
    setRunErrors((errors) => ({ ...errors, [targetId]: '' }));

    try {
      await recover();
      await ApiService.cancelPersistentPrefill(serviceId, container.sessionId, targetId);
      setPersistentContainers(
        (current) =>
          current?.map((item) =>
            item.sessionId === container.sessionId
              ? {
                  ...item,
                  runs: item.runs?.map((itemRun) =>
                    itemRun.runId === targetId ? { ...itemRun, cancelRequested: true } : itemRun
                  )
                }
              : item
          ) ?? current
      );
      await loadPersistentContainers();
    } catch (error: unknown) {
      setRunErrors((errors) => ({ ...errors, [targetId]: getErrorMessage(error) }));
    } finally {
      cancellingRunsRef.current.delete(targetId);
      setCancellingRunIds([...cancellingRunsRef.current]);
    }
  };

  const clearLogins = async () => {
    await recover();
    const response = await ApiService.clearPersistentLogins();
    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER)
      resetPersistentLoginState(getPersistentServiceId(serviceKey));
    if (login.current) login.current.visible = false;
    login.current = null;
    setPersistentLoginTarget(null);
    await Promise.all([loadPersistentContainers(), loadIntegrationLoginAvailability()]);
    return normalizePersistentLoginClearResults(response);
  };
  return {
    persistentContainers,
    containersByServiceKey,
    loadingPersistentContainers,
    persistentError,
    errors,
    errorActions,
    actions,
    authenticatingServiceKeys,
    visibleIntegrationLoginAvailabilityByService,
    visibleIntegrationLoginErrors,
    loadingIntegrationLoginAvailability:
      loadingIntegrationLoginAvailability ||
      (canUseSavedLogin && integrationLoginAvailabilityIdentity !== privateAvailabilityIdentity),
    persistentLoginTarget: visiblePersistentLoginTarget,
    setPersistentLoginTarget,
    handleDismissPersistentLogin,
    loadPersistentContainers,
    loadIntegrationLoginAvailability,
    handleStartPersistent,
    handleStopPersistent,
    handleLogoutPersistent,
    handlePersistentLogin,
    handleCancelPersistentDownload,
    cancellingRunIds,
    runErrors,
    clearLogins,
    recover,
    privateAvailabilityIdentity
  };
}
