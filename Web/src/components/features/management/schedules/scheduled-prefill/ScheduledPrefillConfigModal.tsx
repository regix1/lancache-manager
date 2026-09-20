import { useCallback, useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Modal } from '@components/ui/Modal';
import { Button } from '@components/ui/Button';
import { Alert } from '@components/ui/Alert';
import { CustomScrollbar } from '@components/ui/CustomScrollbar';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import LoadingSpinner from '@components/common/LoadingSpinner';
import ApiService from '@services/api.service';
import { ApiError } from '@services/apiError';
import { GameSelectionModal } from '@components/features/prefill/GameSelectionModal';
import { resolveCachedAppIds } from '@components/features/prefill/cachedApps';
import type { PersistentPrefillContainerDto } from '@components/features/prefill/persistentPrefillTypes';
import { ScheduledPrefillPlatformsPanel } from './ScheduledPrefillPlatformsPanel';
import {
  getPersistentServiceId,
  isScheduledPrefillAnonymousService
} from './scheduledPrefillPlatformUi';
import {
  SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS,
  SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS,
  SCHEDULED_PREFILL_SUPPORTED_PRESETS
} from './constants';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { getErrorMessage, isAbortError } from '@utils/error';
import type {
  ScheduledPrefillEditTarget,
  ScheduledPrefillSchedule,
  ScheduledPrefillServiceKey,
  ScheduledPrefillGameSelectionState
} from './types';

interface ScheduledPrefillConfigModalProps {
  target: ScheduledPrefillEditTarget | null;
  container?: PersistentPrefillContainerDto;
  identity: string;
  onClose: () => void;
  onSaved: (serviceKey: ScheduledPrefillServiceKey, schedule: ScheduledPrefillSchedule) => void;
  onLoaded: (serviceKey: ScheduledPrefillServiceKey, schedule: ScheduledPrefillSchedule) => void;
}
const validateServiceConfig = (
  schedule: ScheduledPrefillSchedule,
  serviceKey: ScheduledPrefillServiceKey,
  serviceName: string,
  t: (key: string, values?: Record<string, string | number>) => string
): string | null => {
  const baseKey = 'management.schedules.services.scheduledPrefill.config';

  if (!schedule.enabled) {
    return null;
  }

  if (!SCHEDULED_PREFILL_SUPPORTED_PRESETS[serviceKey].includes(schedule.preset)) {
    return t(`${baseKey}.validation.unsupportedPreset`, {
      service: serviceName,
      preset: t(`${baseKey}.presets.${schedule.preset.toLowerCase()}`)
    });
  }

  if (schedule.preset === 'Top' && (!schedule.topCount || schedule.topCount < 1)) {
    return t(`${baseKey}.validation.topCount`, { service: serviceName });
  }

  if (
    SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS[serviceKey].length > 0 &&
    schedule.operatingSystems.length === 0
  ) {
    return t(`${baseKey}.validation.operatingSystems`, { service: serviceName });
  }

  if (
    schedule.maxConcurrency.mode === 'Fixed' &&
    (schedule.maxConcurrency.value < SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.min ||
      schedule.maxConcurrency.value > SCHEDULED_PREFILL_MAX_CONCURRENCY_BOUNDS.max)
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
  target,
  container,
  identity,
  onClose,
  onSaved,
  onLoaded
}: ScheduledPrefillConfigModalProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const [config, setConfig] = useState<ScheduledPrefillSchedule | null>(null);
  const [loading, setLoading] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [loadError, setLoadError] = useState<{ key: string; message: string } | null>(null);
  const [missing, setMissing] = useState(false);
  const [discardConfirmOpen, setDiscardConfirmOpen] = useState(false);
  const [overwriteEnabledConfirmOpen, setOverwriteEnabledConfirmOpen] = useState(false);
  const baseline = useRef<string | null>(null);
  const dirty = useRef(false);
  const current = useRef(target);
  current.current = target;
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const containerRef = useRef(container);
  containerRef.current = container;
  const confirmed = useRef({ onSaved, onLoaded });
  confirmed.current = { onSaved, onLoaded };
  const writes = useRef(new Map<string, object>());
  const [gameSelection, setGameSelection] = useState<ScheduledPrefillGameSelectionState | null>(
    null
  );
  const [loadingGameSelectionService, setLoadingGameSelectionService] =
    useState<ScheduledPrefillServiceKey | null>(null);
  const [gameLoaded, setGameLoaded] = useState(false);
  const [gameLoadError, setGameLoadError] = useState<string | null>(null);
  const gameSelectionRef = useRef(gameSelection);
  gameSelectionRef.current = target ? gameSelection : null;
  const gameAuthRef = useRef<{ key: string; authenticated: boolean } | null>(null);
  const gameRequestRef = useRef<{
    key: string;
    controller: AbortController;
    again: boolean;
    promise: Promise<void>;
  } | null>(null);
  const gameSelectionNeedsLogin =
    target !== null &&
    !isScheduledPrefillAnonymousService(target.serviceKey) &&
    (!container?.isRunning || !container.isAuthenticated || container.needsRelogin);
  const { on: onSignalR, off: offSignalR, isConnected } = useSignalR();
  const loadKey = target
    ? `${identity}:${target.serviceKey}:${target.scheduleId ?? 'create'}`
    : null;
  useEffect(() => {
    const controller = new AbortController();
    const opening = target?.opening;
    setConfig(target?.schedule ?? null);
    baseline.current = target?.schedule ? JSON.stringify(target.schedule) : null;
    dirty.current = false;
    setSaving(false);
    setError(null);
    setMissing(false);
    setDiscardConfirmOpen(false);
    setOverwriteEnabledConfirmOpen(false);
    setGameSelection(null);
    gameSelectionRef.current = null;
    gameRequestRef.current?.controller.abort();
    gameRequestRef.current = null;
    setLoadingGameSelectionService(null);
    setGameLoaded(false);
    setGameLoadError(null);
    if (!target || target.create) {
      setLoading(false);
      return;
    }
    const requestKey = `${identityRef.current}:${target.serviceKey}:${target.scheduleId}`;
    setLoading(true);
    void ApiService.getScheduledPrefillConfig(controller.signal)
      .then((result) => {
        if (controller.signal.aborted || current.current?.opening !== opening) return;
        setLoadError((current) => (current?.key === requestKey ? null : current));
        const record = result[target.serviceKey].schedules.find(
          (item) => item.id === target.scheduleId
        );
        if (!record) {
          setMissing(true);
          return;
        }
        if (!dirty.current) {
          confirmed.current.onLoaded(target.serviceKey, record);
          setConfig(record);
          baseline.current = JSON.stringify(record);
        }
      })
      .catch((failure: unknown) => {
        if (
          !controller.signal.aborted &&
          current.current?.opening === opening &&
          !isAbortError(failure)
        )
          setLoadError({
            key: requestKey,
            message: t(`${baseKey}.summaryError`, { error: getErrorMessage(failure) })
          });
      })
      .finally(() => {
        if (!controller.signal.aborted && current.current?.opening === opening) setLoading(false);
      });
    return () => controller.abort();
  }, [target, t]);
  const change = (record: ScheduledPrefillSchedule) => {
    dirty.current = JSON.stringify(record) !== baseline.current;
    setConfig(record);
    setError(null);
  };
  const handleClose = () => {
    if (!saving) onClose();
  };
  const handleCancel = () => {
    if (saving) return;
    if (dirty.current) setDiscardConfirmOpen(true);
    else handleClose();
  };
  const commitSave = async () => {
    if (!target || !config || saving || missing) return;
    const opening = target.opening;
    const key = `${target.serviceKey}:${config.id}`;
    const write = {};
    writes.current.set(key, write);
    setSaving(true);
    setError(null);
    try {
      const result = target.create
        ? await ApiService.createScheduledPrefillSchedule(
            getPersistentServiceId(target.serviceKey),
            config
          )
        : await ApiService.updateScheduledPrefillSchedule(
            getPersistentServiceId(target.serviceKey),
            config
          );
      const saved = result[target.serviceKey].schedules.find((record) => record.id === config.id);
      if (!saved) throw new Error(t(`${baseKey}.records.missing`));
      if (writes.current.get(key) !== write) return;
      confirmed.current.onSaved(target.serviceKey, saved);
      if (current.current?.opening !== opening) return;
      baseline.current = JSON.stringify(saved);
      dirty.current = false;
      setConfig(saved);
      onClose();
    } catch (failure: unknown) {
      if (current.current?.opening === opening)
        setError(t(`${baseKey}.saveError`, { error: getErrorMessage(failure) }));
    } finally {
      if (current.current?.opening === opening) setSaving(false);
    }
  };
  const handleSave = () => {
    if (!config || !target) return;
    const validation = !config.name.trim()
      ? t(`${baseKey}.records.nameRequired`)
      : validateServiceConfig(
          config,
          target.serviceKey,
          t(`${baseKey}.services.${target.serviceKey}`),
          t
        );
    if (validation) {
      setError(validation);
      return;
    }
    if (!target.create && target.schedule?.enabled) setOverwriteEnabledConfirmOpen(true);
    else void commitSave();
  };
  const loadGameSelection = useCallback(
    async (serviceKey: ScheduledPrefillServiceKey, sessionId: string) => {
      const key = `${serviceKey}:${sessionId}`;
      if (`${gameSelectionRef.current?.serviceKey}:${gameSelectionRef.current?.sessionId}` !== key)
        return;
      const activeContainer = containerRef.current;
      if (
        !activeContainer ||
        activeContainer.sessionId !== sessionId ||
        !activeContainer.isRunning ||
        (!isScheduledPrefillAnonymousService(serviceKey) &&
          (!activeContainer.isAuthenticated || activeContainer.needsRelogin))
      )
        return;
      if (gameAuthRef.current?.key === key && !gameAuthRef.current.authenticated) return;
      if (gameRequestRef.current?.key === key) {
        gameRequestRef.current.again = true;
        return gameRequestRef.current.promise;
      }
      gameRequestRef.current?.controller.abort();
      const controller = new AbortController();
      const request = { key, controller, again: false, promise: Promise.resolve() };
      const opening = current.current?.opening;
      const requestIdentity = identityRef.current;
      gameRequestRef.current = request;
      setLoadingGameSelectionService(serviceKey);
      const isCurrent = () =>
        gameRequestRef.current === request &&
        !controller.signal.aborted &&
        opening === current.current?.opening &&
        requestIdentity === identityRef.current &&
        `${gameSelectionRef.current?.serviceKey}:${gameSelectionRef.current?.sessionId}` === key;
      request.promise = (async () => {
        try {
          do {
            request.again = false;
            try {
              const { games, cachedAppIds, outdatedAppIds, unknownAppIds } =
                await ApiService.getPersistentPrefillGames(
                  getPersistentServiceId(serviceKey),
                  controller.signal,
                  sessionId
                );
              if (!isCurrent()) return;

              const normalizedGames: ScheduledPrefillGameSelectionState['games'] = games.map(
                (game) => ({
                  name: game.name,
                  appId: String(game.appId)
                })
              );

              setGameLoaded(true);
              setGameSelection((current) =>
                current?.serviceKey === serviceKey && current.sessionId === sessionId
                  ? {
                      ...current,
                      games: normalizedGames,
                      outdatedAppIds,
                      unknownAppIds,
                      cachedAppIds: resolveCachedAppIds(
                        current.cachedAppIds,
                        cachedAppIds,
                        unknownAppIds
                      )
                    }
                  : current
              );
              setGameLoadError(null);
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
            }
          } while (request.again && isCurrent());
        } finally {
          if (isCurrent()) {
            gameRequestRef.current = null;
            setLoadingGameSelectionService(null);
          }
        }
      })();
      return request.promise;
    },
    [t]
  );

  useEffect(() => {
    if (!gameSelection) {
      gameAuthRef.current = null;
      return;
    }
    if (
      !target ||
      target.serviceKey !== gameSelection.serviceKey ||
      !container ||
      container.sessionId !== gameSelection.sessionId
    ) {
      gameRequestRef.current?.controller.abort();
      gameRequestRef.current = null;
      gameSelectionRef.current = null;
      setGameSelection(null);
      setLoadingGameSelectionService(null);
      setGameLoaded(false);
      gameAuthRef.current = null;
      return;
    }
    const key = `${gameSelection.serviceKey}:${gameSelection.sessionId}`;
    const authenticated =
      container.isRunning &&
      (isScheduledPrefillAnonymousService(gameSelection.serviceKey) ||
        (container.isAuthenticated && !container.needsRelogin));
    const previous = gameAuthRef.current;
    gameAuthRef.current = { key, authenticated };
    if (!authenticated && (previous?.key !== key || previous.authenticated)) {
      gameRequestRef.current?.controller.abort();
      gameRequestRef.current = null;
      setLoadingGameSelectionService(null);
      setGameSelection((current) =>
        current?.serviceKey === gameSelection.serviceKey &&
        current.sessionId === gameSelection.sessionId
          ? { ...current, outdatedAppIds: [], unknownAppIds: current.cachedAppIds }
          : current
      );
    } else if (authenticated && previous?.key === key && !previous.authenticated) {
      void loadGameSelection(gameSelection.serviceKey, gameSelection.sessionId);
    }
  }, [target, container, gameSelection, loadGameSelection]);
  useEffect(() => {
    gameRequestRef.current?.controller.abort();
    gameRequestRef.current = null;
    gameSelectionRef.current = null;
    gameAuthRef.current = null;
    setGameSelection(null);
  }, [identity]);
  useEffect(() => {
    const refresh = () => {
      const selection = gameSelectionRef.current;
      if (selection) void loadGameSelection(selection.serviceKey, selection.sessionId);
    };
    onSignalR('PrefillCacheChanged', refresh);
    return () => {
      offSignalR('PrefillCacheChanged', refresh);
    };
  }, [onSignalR, offSignalR, loadGameSelection]);
  useReconnectRefetch(isConnected, () => {
    const selection = gameSelectionRef.current;
    if (selection) void loadGameSelection(selection.serviceKey, selection.sessionId);
  });
  const handleOpenGameSelection = () => {
    if (!target || !config || saving || !config.enabled) return;
    if (gameSelectionNeedsLogin) return;
    if (!container?.isRunning) {
      setError(t(`${baseKey}.selectedGames.requiresPersistentContainer`));
      return;
    }
    const selection: ScheduledPrefillGameSelectionState = {
      serviceKey: target.serviceKey,
      scheduleId: target.scheduleId,
      sessionId: container.sessionId,
      games: [],
      cachedAppIds: [],
      outdatedAppIds: [],
      unknownAppIds: []
    };
    setError(null);
    setGameLoadError(null);
    setGameLoaded(false);
    gameRequestRef.current?.controller.abort();
    gameRequestRef.current = null;
    gameSelectionRef.current = selection;
    setGameSelection(selection);
    void loadGameSelection(selection.serviceKey, selection.sessionId);
  };
  return (
    <>
      <Modal
        opened={target !== null}
        onClose={handleCancel}
        size="xl"
        bodyFlexLayout
        className="scheduled-prefill-content-dialog scheduled-prefill-focused-dialog"
        title={
          target
            ? t(`${baseKey}.modalTitle`, {
                service: t(`${baseKey}.services.${target.serviceKey}`),
                name: config ? config.name : target.name
              })
            : ''
        }
      >
        <div className="scheduled-prefill-config-modal">
          <p className="text-sm text-themed-muted">{t(`${baseKey}.modalDescription`)}</p>
          <div className="scheduled-prefill-config-modal__scroll-area">
            <CustomScrollbar
              maxHeight="none"
              className="scheduled-prefill-config-modal__viewport"
              radius="none"
            >
              <div className="scheduled-prefill-config-modal__scroll-content">
                {loadError?.key === loadKey && <Alert color="red">{loadError.message}</Alert>}
                {error && <Alert color="red">{error}</Alert>}
                {missing ? (
                  <Alert color="red">{t(`${baseKey}.records.missing`)}</Alert>
                ) : config && target ? (
                  <ScheduledPrefillPlatformsPanel
                    serviceKey={target.serviceKey}
                    config={config}
                    disabled={saving}
                    gameSelectionLoading={loadingGameSelectionService !== null && !gameLoaded}
                    gameSelectionNeedsLogin={gameSelectionNeedsLogin}
                    onChange={change}
                    onSelectGames={handleOpenGameSelection}
                    onClearGames={() => change({ ...config, selectedAppIds: [] })}
                  />
                ) : (
                  loading && <LoadingSpinner inline size="md" />
                )}
              </div>
            </CustomScrollbar>
          </div>
          <div className="scheduled-prefill-config-modal__actions">
            <Button onClick={handleCancel} disabled={saving}>
              {t('common.cancel')}
            </Button>
            <Button
              variant="filled"
              color="primary"
              onClick={handleSave}
              loading={saving}
              disabled={!config || missing || saving}
            >
              {t(`${baseKey}.actions.save`)}
            </Button>
          </div>
        </div>
      </Modal>
      <GameSelectionModal
        opened={gameSelection !== null}
        onClose={() => {
          gameSelectionRef.current = null;
          gameRequestRef.current?.controller.abort();
          gameRequestRef.current = null;
          setGameSelection(null);
          setLoadingGameSelectionService(null);
          setGameLoaded(false);
        }}
        serviceId={gameSelection?.serviceKey ?? ''}
        games={gameSelection?.games ?? []}
        selectedAppIds={config?.selectedAppIds ?? []}
        confirmDiscard
        onSave={async (selectedIds) => {
          if (
            !config ||
            !gameSelection ||
            !target ||
            gameSelection.scheduleId !== target.scheduleId
          )
            return;
          const known = new Set(gameSelection.games.map((game) => game.appId));
          change({
            ...config,
            selectedAppIds: [
              ...new Set([...config.selectedAppIds.filter((id) => !known.has(id)), ...selectedIds])
            ]
          });
        }}
        isLoading={loadingGameSelectionService !== null && !gameLoaded}
        cachedAppIds={gameSelection?.cachedAppIds ?? []}
        outdatedAppIds={gameSelection?.outdatedAppIds ?? []}
        unknownAppIds={gameSelection?.unknownAppIds ?? []}
        error={gameLoadError}
      />
      <ConfirmationModal
        opened={discardConfirmOpen}
        onClose={() => setDiscardConfirmOpen(false)}
        onConfirm={() => {
          setDiscardConfirmOpen(false);
          handleClose();
        }}
        title={t(`${baseKey}.discardChanges.confirmTitle`)}
        confirmLabel={t(`${baseKey}.discardChanges.confirmButton`)}
        confirmColor="red"
      >
        <p>{t(`${baseKey}.discardChanges.confirmBody`)}</p>
      </ConfirmationModal>
      <ConfirmationModal
        opened={overwriteEnabledConfirmOpen}
        onClose={() => setOverwriteEnabledConfirmOpen(false)}
        onConfirm={() => {
          setOverwriteEnabledConfirmOpen(false);
          void commitSave();
        }}
        title={t(`${baseKey}.records.confirmEnabledSaveTitle`)}
        confirmLabel={t(`${baseKey}.actions.save`)}
        loading={saving}
      >
        <p>
          {t(`${baseKey}.records.confirmEnabledSaveBody`, {
            service: target && t(`${baseKey}.services.${target.serviceKey}`),
            name: config?.name
          })}
        </p>
      </ConfirmationModal>
    </>
  );
}
