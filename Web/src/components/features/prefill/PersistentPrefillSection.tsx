import { useCallback, useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import LoadingSpinner from '@components/common/LoadingSpinner';
import Badge from '@components/ui/Badge';
import { Button } from '@components/ui/Button';
import { Card, CardContent } from '@components/ui/Card';
import ApiService from '@services/api.service';
import { formatDateTime } from '@utils/formatters';
import {
  PERSISTENT_PREFILL_GUEST_LIFETIME_BOUNDS,
  PERSISTENT_PREFILL_SERVICES,
  PERSISTENT_PREFILL_VALIDITY_BOUNDS
} from './persistentPrefillConstants';
import type {
  PersistentPrefillContainerDto,
  PersistentPrefillServiceId
} from './persistentPrefillTypes';
import { formatTimeRemaining } from './types';
import './PersistentPrefillSection.css';

type RowActionState = {
  service: PersistentPrefillServiceId;
  action: 'start' | 'stop';
} | null;

const normalizeUtcDateString = (dateString: string): string => {
  if (dateString.endsWith('Z') || /[+-]\d{2}:\d{2}$/.test(dateString)) {
    return dateString;
  }

  return `${dateString}Z`;
};

const getAuthRemainingSeconds = (
  container: PersistentPrefillContainerDto,
  nowMs: number
): number => {
  const expiresAtMs = new Date(normalizeUtcDateString(container.authExpiresAtUtc)).getTime();
  if (Number.isNaN(expiresAtMs)) {
    return Math.max(0, container.authTimeRemainingSeconds);
  }

  return Math.max(0, Math.floor((expiresAtMs - nowMs) / 1000));
};

const isWithinBounds = (value: number, min: number, max: number): boolean =>
  Number.isInteger(value) && value >= min && value <= max;

export function PersistentPrefillSection() {
  const { t } = useTranslation();
  const [containers, setContainers] = useState<PersistentPrefillContainerDto[]>([]);
  const [loadingContainers, setLoadingContainers] = useState(true);
  const [loadingSettings, setLoadingSettings] = useState(true);
  const [rowAction, setRowAction] = useState<RowActionState>(null);
  const [savingSettings, setSavingSettings] = useState(false);
  const [validityDaysInput, setValidityDaysInput] = useState('');
  const [guestLifetimeHoursInput, setGuestLifetimeHoursInput] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [successMessage, setSuccessMessage] = useState<string | null>(null);
  const [nowMs, setNowMs] = useState(() => Date.now());

  const containerByService = useMemo(
    () =>
      new Map<PersistentPrefillServiceId, PersistentPrefillContainerDto>(
        containers.map((container) => [container.service, container])
      ),
    [containers]
  );

  const loadContainers = useCallback(async (signal?: AbortSignal) => {
    setLoadingContainers(true);
    try {
      const nextContainers = await ApiService.getPersistentPrefillContainers(signal);
      setContainers(nextContainers);
    } catch (loadError: unknown) {
      if (signal?.aborted) return;
      setError(loadError instanceof Error ? loadError.message : String(loadError));
    } finally {
      if (!signal?.aborted) {
        setLoadingContainers(false);
      }
    }
  }, []);

  const loadSettings = useCallback(async (signal?: AbortSignal) => {
    setLoadingSettings(true);
    try {
      const [validity, guestLifetime] = await Promise.all([
        ApiService.getPersistentPrefillValidity(signal),
        ApiService.getPersistentPrefillGuestLifetime(signal)
      ]);
      setValidityDaysInput(String(validity.days));
      setGuestLifetimeHoursInput(String(guestLifetime.hours));
    } catch (loadError: unknown) {
      if (signal?.aborted) return;
      setError(loadError instanceof Error ? loadError.message : String(loadError));
    } finally {
      if (!signal?.aborted) {
        setLoadingSettings(false);
      }
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void loadContainers(controller.signal);
    void loadSettings(controller.signal);

    return () => controller.abort();
  }, [loadContainers, loadSettings]);

  useEffect(() => {
    const intervalId = window.setInterval(() => setNowMs(Date.now()), 1000);
    return () => window.clearInterval(intervalId);
  }, []);

  const handleStart = async (service: PersistentPrefillServiceId) => {
    setRowAction({ service, action: 'start' });
    setError(null);
    setSuccessMessage(null);
    try {
      await ApiService.startPersistentPrefillContainer(service);
      await loadContainers();
      setSuccessMessage(t('prefill.persistent.messages.started'));
    } catch (startError: unknown) {
      setError(startError instanceof Error ? startError.message : String(startError));
    } finally {
      setRowAction(null);
    }
  };

  const handleStop = async (service: PersistentPrefillServiceId, sessionId: string) => {
    setRowAction({ service, action: 'stop' });
    setError(null);
    setSuccessMessage(null);
    try {
      await ApiService.stopPersistentPrefillContainer(sessionId);
      await loadContainers();
      setSuccessMessage(t('prefill.persistent.messages.stopped'));
    } catch (stopError: unknown) {
      setError(stopError instanceof Error ? stopError.message : String(stopError));
    } finally {
      setRowAction(null);
    }
  };

  const handleSaveSettings = async () => {
    const validityDays = Number(validityDaysInput);
    const guestLifetimeHours = Number(guestLifetimeHoursInput);

    if (
      !isWithinBounds(
        validityDays,
        PERSISTENT_PREFILL_VALIDITY_BOUNDS.min,
        PERSISTENT_PREFILL_VALIDITY_BOUNDS.max
      ) ||
      !isWithinBounds(
        guestLifetimeHours,
        PERSISTENT_PREFILL_GUEST_LIFETIME_BOUNDS.min,
        PERSISTENT_PREFILL_GUEST_LIFETIME_BOUNDS.max
      )
    ) {
      setError(t('prefill.persistent.errors.invalidSettings'));
      setSuccessMessage(null);
      return;
    }

    setSavingSettings(true);
    setError(null);
    setSuccessMessage(null);
    try {
      await Promise.all([
        ApiService.updatePersistentPrefillValidity({ days: validityDays }),
        ApiService.updatePersistentPrefillGuestLifetime({ hours: guestLifetimeHours })
      ]);
      setSuccessMessage(t('prefill.persistent.messages.settingsSaved'));
    } catch (saveError: unknown) {
      setError(saveError instanceof Error ? saveError.message : String(saveError));
    } finally {
      setSavingSettings(false);
    }
  };

  return (
    <Card padding="lg" className="persistent-prefill-section">
      <div className="persistent-prefill-section__header">
        <div>
          <h2 className="persistent-prefill-section__title">{t('prefill.persistent.title')}</h2>
          <p className="persistent-prefill-section__subtitle">{t('prefill.persistent.subtitle')}</p>
        </div>
        {loadingContainers && (
          <span className="persistent-prefill-section__loading">
            <LoadingSpinner inline size="sm" />
            {t('prefill.persistent.loading')}
          </span>
        )}
      </div>

      {error && <div className="persistent-prefill-section__message--error">{error}</div>}
      {successMessage && (
        <div className="persistent-prefill-section__message--success">{successMessage}</div>
      )}

      <CardContent className="persistent-prefill-section__content">
        <div className="persistent-prefill-section__list">
          {PERSISTENT_PREFILL_SERVICES.map((serviceConfig) => {
            const container = containerByService.get(serviceConfig.service);
            const isRunning = container !== undefined && container.isRunning;
            const isRowBusy = rowAction !== null && rowAction.service === serviceConfig.service;
            const remainingSeconds =
              container !== undefined ? getAuthRemainingSeconds(container, nowMs) : 0;
            const expiresAt =
              container !== undefined ? formatDateTime(container.authExpiresAtUtc) : '';
            const needsRelogin =
              container !== undefined && (container.needsRelogin || remainingSeconds <= 0);

            return (
              <div
                key={serviceConfig.service}
                className={`persistent-prefill-service ${serviceConfig.rowClassName}`}
              >
                <div className="persistent-prefill-service__accent" />
                <div className="persistent-prefill-service__main">
                  <div className="persistent-prefill-service__meta">
                    <p className="persistent-prefill-service__name">{t(serviceConfig.labelKey)}</p>
                    <Badge
                      variant={isRunning ? 'success' : 'neutral'}
                      className="persistent-prefill-service__badge"
                    >
                      {isRunning
                        ? t('prefill.persistent.states.running')
                        : t('prefill.persistent.states.stopped')}
                    </Badge>
                  </div>
                  <div className="persistent-prefill-service__details">
                    {isRunning && container !== undefined ? (
                      <>
                        <span className="persistent-prefill-service__countdown">
                          {t('prefill.persistent.authCountdown', {
                            time: formatTimeRemaining(remainingSeconds)
                          })}
                        </span>
                        <span className="persistent-prefill-service__expiry">
                          {t('prefill.persistent.authExpiresAt', { date: expiresAt })}
                        </span>
                      </>
                    ) : (
                      <span className="persistent-prefill-service__expiry">
                        {t('prefill.persistent.notRunning')}
                      </span>
                    )}
                    {needsRelogin && (
                      <span className="persistent-prefill-service__warning">
                        {t('prefill.persistent.needsRelogin')}
                      </span>
                    )}
                  </div>
                </div>
                <div className="persistent-prefill-service__actions">
                  {isRunning && container !== undefined ? (
                    <Button
                      type="button"
                      variant="outline"
                      color="red"
                      size="sm"
                      loading={isRowBusy && rowAction?.action === 'stop'}
                      disabled={rowAction !== null}
                      onClick={() => void handleStop(serviceConfig.service, container.sessionId)}
                    >
                      {t('prefill.persistent.actions.stop')}
                    </Button>
                  ) : (
                    <Button
                      type="button"
                      variant="filled"
                      color="green"
                      size="sm"
                      loading={isRowBusy && rowAction?.action === 'start'}
                      disabled={rowAction !== null}
                      onClick={() => void handleStart(serviceConfig.service)}
                    >
                      {t('prefill.persistent.actions.start')}
                    </Button>
                  )}
                </div>
              </div>
            );
          })}
        </div>

        <div className="persistent-prefill-settings">
          <div className="persistent-prefill-settings__fields">
            <label className="persistent-prefill-settings__field">
              <span>{t('prefill.persistent.settings.validityDays')}</span>
              <input
                type="number"
                min={PERSISTENT_PREFILL_VALIDITY_BOUNDS.min}
                max={PERSISTENT_PREFILL_VALIDITY_BOUNDS.max}
                value={validityDaysInput}
                onChange={(event) => setValidityDaysInput(event.target.value)}
                className="themed-input persistent-prefill-settings__input"
                disabled={loadingSettings || savingSettings}
              />
            </label>
            <label className="persistent-prefill-settings__field">
              <span>{t('prefill.persistent.settings.guestLifetimeHours')}</span>
              <input
                type="number"
                min={PERSISTENT_PREFILL_GUEST_LIFETIME_BOUNDS.min}
                max={PERSISTENT_PREFILL_GUEST_LIFETIME_BOUNDS.max}
                value={guestLifetimeHoursInput}
                onChange={(event) => setGuestLifetimeHoursInput(event.target.value)}
                className="themed-input persistent-prefill-settings__input"
                disabled={loadingSettings || savingSettings}
              />
            </label>
          </div>
          <Button
            type="button"
            variant="filled"
            size="sm"
            loading={savingSettings}
            disabled={loadingSettings}
            onClick={() => void handleSaveSettings()}
            className="persistent-prefill-settings__save"
          >
            {t('prefill.persistent.actions.saveSettings')}
          </Button>
        </div>
      </CardContent>
    </Card>
  );
}
