import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { Card } from '@components/ui/Card';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { GameSelectionModal } from '@components/features/prefill/GameSelectionModal';
import {
  PERSISTENT_PREFILL_SERVICES,
  PERSISTENT_PREFILL_VALIDITY_BOUNDS
} from '@components/features/prefill/persistentPrefillConstants';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from '@components/features/prefill/persistentPrefillTypes';
import {
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import { ScheduledPrefillAuthStatus } from './ScheduledPrefillAuthStatus';
import { PersistentLoginHost } from './PersistentLoginHost';
import { ScheduledPrefillServiceRow } from './ScheduledPrefillServiceRow';
import { waitForPersistentContainerAuth } from './waitForPersistentContainerAuth';
import type {
  ScheduledPrefillAuthStatusItem,
  ScheduledPrefillConfigDto,
  ScheduledPrefillServiceConfigDto,
  ScheduledPrefillServiceKey
} from './types';
import { getErrorMessage, isAbortError } from '@utils/error';

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

interface ScheduledPrefillPersistentAction {
  serviceKey: ScheduledPrefillServiceKey;
  action: 'start' | 'stop';
}

interface NumericBounds {
  min: number;
  max: number;
}

const DEFAULT_PERSISTENT_PREFILL_VALIDITY_DAYS = 90;

const getPersistentServiceId = (
  serviceKey: ScheduledPrefillServiceKey
): PersistentPrefillServiceId => {
  const service = PERSISTENT_PREFILL_SERVICES.find((item) => item.key === serviceKey);
  if (!service) {
    throw new Error(`Unknown scheduled prefill service: ${serviceKey}`);
  }

  return service.service;
};

const clampToBounds = (value: number, bounds: NumericBounds): number =>
  Math.min(bounds.max, Math.max(bounds.min, Math.trunc(value)));

const parseBoundedInteger = (value: string, bounds: NumericBounds, fallback: number): number => {
  const parsed = Number(value);
  return Number.isFinite(parsed) ? clampToBounds(parsed, bounds) : fallback;
};

const validateServiceConfig = (
  serviceConfig: ScheduledPrefillServiceConfigDto,
  serviceName: string,
  t: (key: string, values?: Record<string, string | number>) => string
): string | null => {
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  if (!serviceConfig.enabled) {
    return null;
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
  const [authStatuses, setAuthStatuses] = useState<ScheduledPrefillAuthStatusItem[]>([]);
  const [loadingConfig, setLoadingConfig] = useState(false);
  const [loadingAuthStatus, setLoadingAuthStatus] = useState(false);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [validationError, setValidationError] = useState<string | null>(null);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [authError, setAuthError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [persistentContainers, setPersistentContainers] = useState<PersistentPrefillContainerDto[]>(
    []
  );
  const [loadingPersistentContainers, setLoadingPersistentContainers] = useState(false);
  const [persistentError, setPersistentError] = useState<string | null>(null);
  const [persistentAction, setPersistentAction] = useState<ScheduledPrefillPersistentAction | null>(
    null
  );
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
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  const loadConfig = useCallback(async (signal?: AbortSignal) => {
    setLoadingConfig(true);
    try {
      const nextConfig = await ApiService.getScheduledPrefillConfig(signal);
      setConfig(nextConfig);
      setLoadError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setLoadError(getErrorMessage(error));
      }
    } finally {
      setLoadingConfig(false);
    }
  }, []);

  const loadAuthStatus = useCallback(async (signal?: AbortSignal) => {
    setLoadingAuthStatus(true);
    try {
      const nextStatuses = await ApiService.getScheduledPrefillAuthStatus(signal);
      setAuthStatuses(nextStatuses);
      setAuthError(null);
    } catch (error: unknown) {
      if (!isAbortError(error)) {
        setAuthError(getErrorMessage(error));
      }
    } finally {
      setLoadingAuthStatus(false);
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
    setAuthError(null);
    setPersistentError(null);
    setGlobalSettingsError(null);
    setGlobalSettingsSaved(false);
    setGameSelectionError(null);
    setGameSelection(null);
    setPersistentLoginTarget(null);
    void loadConfig(controller.signal);
    void loadAuthStatus(controller.signal);
    void loadPersistentContainers(controller.signal);
    void loadGlobalSettings(controller.signal);

    return () => {
      controller.abort();
    };
  }, [opened, loadConfig, loadAuthStatus, loadPersistentContainers, loadGlobalSettings]);

  const isLoading = loadingConfig || loadingAuthStatus;
  const hasInitialData = config !== null;

  const persistentContainerByService = useMemo(
    () =>
      new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
        persistentContainers.map((container) => [container.service, container])
      ),
    [persistentContainers]
  );

  const validationMessage = useMemo(() => {
    if (!config) {
      return null;
    }

    for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
      const serviceName = t(`${baseKey}.services.${serviceKey}`);
      const error = validateServiceConfig(config[serviceKey], serviceName, t);
      if (error) {
        return error;
      }
    }

    return null;
  }, [config, t, baseKey]);

  const handleServiceChange = (
    serviceKey: ScheduledPrefillServiceKey,
    serviceConfig: ScheduledPrefillServiceConfigDto
  ) => {
    setConfig((current) => (current ? { ...current, [serviceKey]: serviceConfig } : current));
    setValidationError(null);
    setSaveError(null);
    setAuthError(null);
  };

  const clearGlobalSettingsNotice = () => {
    setGlobalSettingsError(null);
    setGlobalSettingsSaved(false);
  };

  const handlePersistentValidityDaysChange = (value: string) => {
    setPersistentValidityDays((current) =>
      parseBoundedInteger(value, PERSISTENT_PREFILL_VALIDITY_BOUNDS, current)
    );
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
    } catch (error: unknown) {
      setGlobalSettingsError(getErrorMessage(error));
    } finally {
      setSavingGlobalSettings(false);
    }
  };

  const handleStartPersistent = async (serviceKey: ScheduledPrefillServiceKey) => {
    setPersistentAction({ serviceKey, action: 'start' });
    setPersistentError(null);

    try {
      const serviceId = getPersistentServiceId(serviceKey);
      await ApiService.startPersistentPrefillContainer(serviceId);
      const { containers, container } = await waitForPersistentContainerAuth(serviceId);
      setPersistentContainers(containers);
      setPersistentError(null);
      if (container?.isRunning && !container.isAuthenticated) {
        setPersistentLoginTarget(serviceKey);
      }
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
      await loadPersistentContainers();
    } catch (error: unknown) {
      setPersistentError(getErrorMessage(error));
    } finally {
      setPersistentAction(null);
    }
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
      const { containers, container: refreshed } = await waitForPersistentContainerAuth(serviceId, {
        maxAttempts: 6,
        intervalMs: 500
      });
      setPersistentContainers(containers);
      container = refreshed ?? container;
    }

    if (!container.isAuthenticated) {
      setGameSelectionError(null);
      setPersistentLoginTarget(serviceKey);
      return;
    }

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
    setAuthError(null);
    setGameSelectionError(null);
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
            <div className="scheduled-prefill-config-modal__loading">
              <LoadingSpinner size="lg" />
              <span>{t(`${baseKey}.loading`)}</span>
            </div>
          ) : (
            <>
              {loadError && (
                <Alert color="red" className="scheduled-prefill-config-modal__alert">
                  {t(`${baseKey}.loadError`, { error: loadError })}
                </Alert>
              )}
              {validationError && (
                <Alert color="yellow" className="scheduled-prefill-config-modal__alert">
                  {validationError}
                </Alert>
              )}
              {saveError && (
                <Alert color="red" className="scheduled-prefill-config-modal__alert">
                  {t(`${baseKey}.saveError`, { error: saveError })}
                </Alert>
              )}
              {authError && (
                <Alert color="red" className="scheduled-prefill-config-modal__alert">
                  {t(`${baseKey}.authError`, { error: authError })}
                </Alert>
              )}
              {persistentError && (
                <Alert color="red" className="scheduled-prefill-config-modal__alert">
                  {t(`${baseKey}.persistentContainer.error`, { error: persistentError })}
                </Alert>
              )}
              {gameSelectionError && (
                <Alert color="red" className="scheduled-prefill-config-modal__alert">
                  {t(`${baseKey}.selectedGames.error`, { error: gameSelectionError })}
                </Alert>
              )}

              <Card padding="md" className="scheduled-prefill-config-modal__intro">
                <h3 className="scheduled-prefill-config-modal__section-title">
                  {t(`${baseKey}.modalTitle`)}
                </h3>
                <p className="scheduled-prefill-config-modal__description">
                  {t(`${baseKey}.modalDescription`)}
                </p>
              </Card>

              <ScheduledPrefillAuthStatus
                statuses={authStatuses}
                loading={loadingAuthStatus}
                disabled={saving || savingGlobalSettings}
                onRefresh={() => loadAuthStatus()}
                onError={setAuthError}
              />

              <Card padding="md" className="scheduled-prefill-config-modal__settings">
                <div className="scheduled-prefill-config-modal__section-header">
                  <div>
                    <h3 className="scheduled-prefill-config-modal__section-title">
                      {t(`${baseKey}.settings.title`)}
                    </h3>
                    <p className="scheduled-prefill-config-modal__description">
                      {t(`${baseKey}.settings.description`)}
                    </p>
                  </div>
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

                {globalSettingsSaved && (
                  <Alert color="green" className="scheduled-prefill-config-modal__alert">
                    {t(`${baseKey}.settings.saved`)}
                  </Alert>
                )}
                {globalSettingsError && (
                  <Alert color="red" className="scheduled-prefill-config-modal__alert">
                    {t(`${baseKey}.settings.error`, { error: globalSettingsError })}
                  </Alert>
                )}

                <div className="scheduled-prefill-config-modal__settings-grid">
                  <label className="scheduled-prefill-config-modal__settings-field">
                    <span className="scheduled-prefill-config-modal__settings-label">
                      {t(`${baseKey}.settings.persistentValidityLabel`)}
                    </span>
                    <input
                      type="number"
                      min={PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                      max={PERSISTENT_PREFILL_VALIDITY_BOUNDS.max}
                      step={1}
                      className="themed-input scheduled-prefill-config-modal__settings-input"
                      value={persistentValidityDays}
                      disabled={loadingGlobalSettings || savingGlobalSettings}
                      onChange={(event) => handlePersistentValidityDaysChange(event.target.value)}
                    />
                    <p className="scheduled-prefill-config-modal__settings-help">
                      {t(`${baseKey}.settings.persistentValidityHelp`, {
                        min: PERSISTENT_PREFILL_VALIDITY_BOUNDS.min,
                        max: PERSISTENT_PREFILL_VALIDITY_BOUNDS.max
                      })}
                    </p>
                  </label>
                </div>

                <div className="scheduled-prefill-config-modal__settings-actions">
                  <Button
                    type="button"
                    variant="filled"
                    color="green"
                    onClick={() => void handleSaveGlobalSettings()}
                    disabled={loadingGlobalSettings || savingGlobalSettings}
                    loading={savingGlobalSettings}
                  >
                    {savingGlobalSettings
                      ? t(`${baseKey}.settings.saving`)
                      : t(`${baseKey}.settings.save`)}
                  </Button>
                </div>
              </Card>

              {config ? (
                <section className="scheduled-prefill-config-modal__services">
                  <div className="scheduled-prefill-config-modal__section-header">
                    <h3 className="scheduled-prefill-config-modal__section-title">
                      {t(`${baseKey}.servicesTitle`)}
                    </h3>
                    {loadingConfig && (
                      <span className="scheduled-prefill-config-modal__inline-loading">
                        <LoadingSpinner inline size="sm" />
                        {t(`${baseKey}.loading`)}
                      </span>
                    )}
                  </div>
                  <div className="scheduled-prefill-config-modal__rows">
                    {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
                      const persistentServiceId = getPersistentServiceId(serviceKey);
                      const rowPersistentAction =
                        persistentAction?.serviceKey === serviceKey
                          ? persistentAction.action
                          : null;

                      return (
                        <div key={serviceKey} className="scheduled-prefill-config-modal__row">
                          <ScheduledPrefillServiceRow
                            serviceKey={serviceKey}
                            config={config[serviceKey]}
                            disabled={saving || loadingConfig}
                            persistentContainer={persistentContainerByService.get(
                              persistentServiceId
                            )}
                            persistentStatusLoading={loadingPersistentContainers}
                            persistentAction={rowPersistentAction}
                            gameSelectionLoading={loadingGameSelectionService === serviceKey}
                            onChange={(serviceConfig) =>
                              handleServiceChange(serviceKey, serviceConfig)
                            }
                            onStartPersistent={() => void handleStartPersistent(serviceKey)}
                            onStopPersistent={() => void handleStopPersistent(serviceKey)}
                            onSelectGames={() => handleOpenGameSelection(serviceKey)}
                          />
                        </div>
                      );
                    })}
                  </div>
                </section>
              ) : (
                <div className="scheduled-prefill-config-modal__empty">{t(`${baseKey}.empty`)}</div>
              )}
            </>
          )}

          <div className="scheduled-prefill-config-modal__actions">
            <Button
              type="button"
              variant="default"
              onClick={handleClose}
              disabled={saving || savingGlobalSettings}
            >
              {t('common.cancel')}
            </Button>
            <Button
              type="button"
              variant="filled"
              color="green"
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
          onAuthenticated={() => void loadPersistentContainers()}
          onDismiss={() => setPersistentLoginTarget(null)}
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
