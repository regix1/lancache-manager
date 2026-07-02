import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import Badge from '@components/ui/Badge';
import { HelpPopover } from '@components/ui/HelpPopover';
import LoadingSpinner from '@components/common/LoadingSpinner';
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
  SCHEDULED_PREFILL_SUPPORTED_PRESETS
} from './constants';
import { ScheduledPrefillPlatformsPanel } from './ScheduledPrefillPlatformsPanel';
import { getPersistentServiceId, needsPersistentLogin } from './scheduledPrefillPlatformUi';
import { PersistentLoginHost } from './PersistentLoginHost';
import type { ScheduledPrefillPersistentActionState } from './scheduledPrefillPersistentTypes';
import { waitForPersistentContainerAuth } from './waitForPersistentContainerAuth';
import { usePersistentPrefillContainerSignalR } from './usePersistentPrefillContainerSignalR';
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

const reconcileScheduledPrefillConfig = (
  rawConfig: ScheduledPrefillConfigDto
): ScheduledPrefillConfigDto => ({
  ...rawConfig,
  steam: reconcileServiceConfigPreset('steam', rawConfig.steam),
  epic: reconcileServiceConfigPreset('epic', rawConfig.epic),
  xbox: reconcileServiceConfigPreset('xbox', rawConfig.xbox),
  battleNet: reconcileServiceConfigPreset('battleNet', rawConfig.battleNet),
  riot: reconcileServiceConfigPreset('riot', rawConfig.riot)
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

  if (serviceConfig.operatingSystems.length === 0) {
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
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [persistentContainers, setPersistentContainers] = useState<PersistentPrefillContainerDto[]>(
    []
  );
  const [loadingPersistentContainers, setLoadingPersistentContainers] = useState(false);
  const [persistentError, setPersistentError] = useState<string | null>(null);
  const [persistentAction, setPersistentAction] =
    useState<ScheduledPrefillPersistentActionState | null>(null);
  const [persistentValidityDays, setPersistentValidityDays] = useState(
    DEFAULT_PERSISTENT_PREFILL_VALIDITY_DAYS
  );
  const [loadingGlobalSettings, setLoadingGlobalSettings] = useState(false);
  const [savingGlobalSettings, setSavingGlobalSettings] = useState(false);
  const [globalSettingsError, setGlobalSettingsError] = useState<string | null>(null);
  const [globalSettingsSaved, setGlobalSettingsSaved] = useState(false);
  const [gameSelection, setGameSelection] = useState<ScheduledPrefillGameSelectionState | null>(
    null
  );
  const [loadingGameSelectionService, setLoadingGameSelectionService] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const [gameSelectionError, setGameSelectionError] = useState<string | null>(null);
  const [persistentLoginTarget, setPersistentLoginTarget] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const [persistentAuthPendingKeys, setPersistentAuthPendingKeys] = useState<
    ScheduledPrefillServiceKey[]
  >([]);
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  // Auto-dismiss the "settings saved" notice so it does not linger forever.
  const scheduleGlobalSavedDismiss = useTimeoutCallback(2500);

  const markPersistentAuthPending = useCallback((serviceKey: ScheduledPrefillServiceKey) => {
    setPersistentAuthPendingKeys((current) =>
      current.includes(serviceKey) ? current : [...current, serviceKey]
    );
  }, []);

  const clearPersistentAuthPending = useCallback((serviceKey: ScheduledPrefillServiceKey) => {
    setPersistentAuthPendingKeys((current) => current.filter((key) => key !== serviceKey));
  }, []);

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

  const loadPersistentContainers = useCallback(async (signal?: AbortSignal) => {
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
    if (!opened) {
      return;
    }

    const controller = new AbortController();
    setValidationError(null);
    setSaveError(null);
    setPersistentError(null);
    setGlobalSettingsError(null);
    setGlobalSettingsSaved(false);
    setGameSelectionError(null);
    setGameSelection(null);
    setPersistentLoginTarget(null);
    setPersistentAuthPendingKeys([]);
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
        persistentContainers.map((container) => [container.service, container])
      ),
    [persistentContainers]
  );

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

  useEffect(() => {
    if (!opened) {
      return;
    }

    for (const service of PERSISTENT_PREFILL_SERVICES) {
      const container = persistentContainerByService.get(service.service);
      if (container?.isRunning && container.isAuthenticated) {
        clearPersistentAuthPending(service.key);
      }
    }
  }, [opened, persistentContainerByService, clearPersistentAuthPending]);

  const shouldWatchPersistentAuth = useMemo(
    () =>
      persistentContainers.some((container) => container.isRunning && !container.isAuthenticated) ||
      persistentLoginTarget !== null ||
      persistentAuthPendingKeys.length > 0,
    [persistentContainers, persistentLoginTarget, persistentAuthPendingKeys]
  );

  const { signalR } = usePersistentPrefillContainerSignalR({
    enabled: opened && shouldWatchPersistentAuth,
    onRefresh: () => {
      void loadPersistentContainers();
    }
  });

  const authenticatingServiceKeys = useMemo(
    () =>
      Array.from(
        new Set([
          ...persistentAuthPendingKeys,
          ...(persistentLoginTarget ? [persistentLoginTarget] : [])
        ])
      ),
    [persistentAuthPendingKeys, persistentLoginTarget]
  );

  const isLoading = loadingConfig;
  const hasInitialData = config !== null;

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
  const banner = useMemo<{ color: 'red' | 'yellow'; message: string } | null>(() => {
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
    return null;
  }, [
    loadError,
    globalSettingsError,
    saveError,
    persistentError,
    gameSelectionError,
    validationError,
    t,
    baseKey
  ]);

  const handleServiceChange = (
    serviceKey: ScheduledPrefillServiceKey,
    serviceConfig: ScheduledPrefillServiceConfigDto
  ) => {
    setConfig((current) => (current ? { ...current, [serviceKey]: serviceConfig } : current));
    setValidationError(null);
    setSaveError(null);
  };

  const clearGlobalSettingsNotice = () => {
    setGlobalSettingsError(null);
    setGlobalSettingsSaved(false);
  };

  const handlePersistentValidityDaysChange = (value: number) => {
    setPersistentValidityDays(clampToBounds(value, PERSISTENT_PREFILL_VALIDITY_BOUNDS));
    clearGlobalSettingsNotice();
  };

  const handleSaveGlobalSettings = async () => {
    const nextValidityDays = clampToBounds(
      persistentValidityDays,
      PERSISTENT_PREFILL_VALIDITY_BOUNDS
    );

    setSavingGlobalSettings(true);
    setGlobalSettingsError(null);
    setGlobalSettingsSaved(false);

    try {
      await ApiService.updatePersistentPrefillValidity({ days: nextValidityDays });
      setPersistentValidityDays(nextValidityDays);
      setGlobalSettingsSaved(true);
      scheduleGlobalSavedDismiss(() => setGlobalSettingsSaved(false));
    } catch (error: unknown) {
      setGlobalSettingsError(getErrorMessage(error));
    } finally {
      setSavingGlobalSettings(false);
    }
  };

  const handleStartPersistent = async (serviceKey: ScheduledPrefillServiceKey) => {
    setPersistentAction({ serviceKey, action: 'start' });
    setPersistentError(null);
    markPersistentAuthPending(serviceKey);

    try {
      const serviceId = getPersistentServiceId(serviceKey);
      await ApiService.startPersistentPrefillContainer(serviceId);
      const { containers, container } = await waitForPersistentContainerAuth(serviceId, {
        signalR,
        onContainersUpdate: (nextContainers) => setPersistentContainers(nextContainers)
      });
      setPersistentContainers(containers);
      setPersistentError(null);
      if (container?.isRunning && !container.isAuthenticated) {
        setPersistentLoginTarget(serviceKey);
      } else {
        clearPersistentAuthPending(serviceKey);
      }
    } catch (error: unknown) {
      clearPersistentAuthPending(serviceKey);
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
      await loadPersistentContainers();
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
    markPersistentAuthPending(serviceKey);
    setPersistentLoginTarget(serviceKey);
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

  const handleOpenGameSelection = async (serviceKey: ScheduledPrefillServiceKey) => {
    const serviceId = getPersistentServiceId(serviceKey);
    let container = persistentContainerByService.get(serviceId);
    if (!container?.isRunning) {
      setGameSelectionError(t(`${baseKey}.selectedGames.requiresPersistentContainer`));
      return;
    }

    if (!container.isAuthenticated) {
      markPersistentAuthPending(serviceKey);
      const { containers, container: refreshed } = await waitForPersistentContainerAuth(serviceId, {
        signalR,
        timeoutMs: 6_000,
        onContainersUpdate: (nextContainers) => setPersistentContainers(nextContainers)
      });
      setPersistentContainers(containers);
      container = refreshed ?? container;
      if (container.isAuthenticated) {
        clearPersistentAuthPending(serviceKey);
      }
    }

    if (!container.isAuthenticated) {
      setGameSelectionError(null);
      setPersistentLoginTarget(serviceKey);
      return;
    }

    clearPersistentAuthPending(serviceKey);
    setGameSelection({
      serviceKey,
      sessionId: container.sessionId,
      games: [],
      cachedAppIds: []
    });
    void loadGameSelection(serviceKey, container.sessionId);
  };

  const handleSaveGameSelection = async (selectedIds: string[]) => {
    if (!gameSelection) {
      return;
    }

    const selectedAppIds = Array.from(new Set(selectedIds.map((selectedId) => String(selectedId))));
    setConfig((current) =>
      current
        ? {
            ...current,
            [gameSelection.serviceKey]: {
              ...current[gameSelection.serviceKey],
              selectedAppIds
            }
          }
        : current
    );
    setValidationError(null);
    setSaveError(null);
    setGameSelectionError(null);

    const serviceId = getPersistentServiceId(gameSelection.serviceKey);
    const container = persistentContainerByService.get(serviceId);
    if (container?.isRunning && container.isAuthenticated) {
      try {
        await ApiService.setPersistentPrefillSelectedApps(serviceId, selectedAppIds);
      } catch (error: unknown) {
        setGameSelectionError(getErrorMessage(error));
      }
    }
  };

  const handlePersistentDownload = async (serviceKey: ScheduledPrefillServiceKey) => {
    if (!config) {
      return;
    }

    const serviceConfig = config[serviceKey];
    const serviceId = getPersistentServiceId(serviceKey);
    const container = persistentContainerByService.get(serviceId);

    if (!container?.isRunning || !container.isAuthenticated) {
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
      await ApiService.updateScheduledPrefillConfig(config);
      await Promise.resolve(onSaved?.());
      onClose();
    } catch (error: unknown) {
      setSaveError(getErrorMessage(error));
    } finally {
      setSaving(false);
    }
  };

  const handleClose = () => {
    if (!saving && !savingGlobalSettings) {
      setGameSelection(null);
      onClose();
    }
  };

  return (
    <>
      <Modal opened={opened} onClose={handleClose} title={t(`${baseKey}.title`)} size="full">
        <div className="scheduled-prefill-config-modal">
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
                    {hasPersistentLoginWarning && (
                      <Badge variant="warning">{t(`${baseKey}.authWarning`)}</Badge>
                    )}
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

                <div className="scheduled-prefill-config-modal__global">
                  <div className="scheduled-prefill-config-modal__global-row">
                    <label
                      className="scheduled-prefill-config-modal__global-label"
                      htmlFor="scheduled-prefill-persistent-validity-days"
                    >
                      {t(`${baseKey}.settings.persistentValidityLabel`)}
                    </label>
                    <NumberInput
                      id="scheduled-prefill-persistent-validity-days"
                      className="scheduled-prefill-number-cap"
                      min={PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                      max={PERSISTENT_PREFILL_VALIDITY_BOUNDS.max}
                      step={1}
                      value={persistentValidityDays}
                      disabled={loadingGlobalSettings || savingGlobalSettings}
                      aria-label={t(`${baseKey}.settings.persistentValidityLabel`)}
                      onChange={handlePersistentValidityDaysChange}
                    />
                    <Button
                      type="button"
                      variant="subtle"
                      size={SCHEDULED_PREFILL_BUTTON_SIZE}
                      onClick={() => void handleSaveGlobalSettings()}
                      disabled={loadingGlobalSettings || savingGlobalSettings}
                      loading={savingGlobalSettings}
                    >
                      {savingGlobalSettings
                        ? t(`${baseKey}.settings.saving`)
                        : t(`${baseKey}.settings.save`)}
                    </Button>
                    {globalSettingsSaved && (
                      <span className="scheduled-prefill-config-modal__global-saved" role="status">
                        {t(`${baseKey}.settings.saved`)}
                      </span>
                    )}
                    {(loadingGlobalSettings || savingGlobalSettings) && (
                      <span className="scheduled-prefill-config-modal__inline-loading">
                        <LoadingSpinner inline size="sm" />
                        {t(
                          savingGlobalSettings
                            ? `${baseKey}.settings.saving`
                            : `${baseKey}.settings.loading`
                        )}
                      </span>
                    )}
                  </div>
                  <p className="scheduled-prefill-config-modal__global-help">
                    {t(`${baseKey}.settings.persistentValidityHelp`, {
                      min: PERSISTENT_PREFILL_VALIDITY_BOUNDS.min,
                      max: PERSISTENT_PREFILL_VALIDITY_BOUNDS.max
                    })}
                  </p>
                </div>
              </div>

              {banner && (
                <Alert color={banner.color} className="scheduled-prefill-config-modal__alert">
                  {banner.message}
                </Alert>
              )}

              {config ? (
                <ScheduledPrefillPlatformsPanel
                  config={config}
                  disabled={saving || loadingConfig}
                  statusLoading={loadingPersistentContainers}
                  containersByServiceKey={containersByServiceKey}
                  selectedGamesCountByServiceKey={selectedGamesCountByServiceKey}
                  persistentAction={persistentAction}
                  authenticatingServiceKeys={authenticatingServiceKeys}
                  gameSelectionLoadingServiceKey={loadingGameSelectionService}
                  onServiceChange={handleServiceChange}
                  onStart={(serviceKey) => void handleStartPersistent(serviceKey)}
                  onStop={(serviceKey) => void handleStopPersistent(serviceKey)}
                  onLogin={handlePersistentLogin}
                  onSelectGames={(serviceKey) => void handleOpenGameSelection(serviceKey)}
                  onDownload={(serviceKey) => void handlePersistentDownload(serviceKey)}
                  onCancelDownload={(serviceKey) => void handleCancelPersistentDownload(serviceKey)}
                />
              ) : (
                <div className="scheduled-prefill-config-modal__empty">{t(`${baseKey}.empty`)}</div>
              )}
            </>
          )}

          <div className="scheduled-prefill-config-modal__actions">
            <Button
              type="button"
              variant="default"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={handleClose}
              disabled={saving || savingGlobalSettings}
            >
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="filled"
              color="green"
              size={SCHEDULED_PREFILL_BUTTON_SIZE}
              onClick={handleSave}
              disabled={!config || saving || loadingConfig || savingGlobalSettings}
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
            clearPersistentAuthPending(persistentLoginTarget);
            void loadPersistentContainers();
          }}
          onDismiss={() => {
            clearPersistentAuthPending(persistentLoginTarget);
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
    </>
  );
}
