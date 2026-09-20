import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import {
  ActionMenu,
  ActionMenuDangerItem,
  ActionMenuDivider,
  ActionMenuGroup,
  ActionMenuItem
} from '@components/ui/ActionMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { ChevronDown } from 'lucide-react';
import StatusDot from '@components/common/StatusDot';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import {
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import ScheduleIntervalPicker from '../ScheduleIntervalPicker';
import { formatLastRun } from '../scheduleFormatting';
import type { CustomSchedule } from '../custom-schedule/types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import {
  getPersistentServiceId,
  isScheduledPrefillAccountService,
  SCHEDULED_PREFILL_PLATFORM_UI
} from './scheduledPrefillPlatformUi';
import type {
  ScheduledPrefillConfigDto,
  ScheduledPrefillRowLoginState,
  ScheduledPrefillServiceId,
  ScheduledPrefillServiceKey,
  ScheduledPrefillServiceScheduleDto,
  ScheduledPrefillSchedule,
  ScheduledPrefillEditTarget
} from './types';
import { getErrorMessage, isAbortError } from '@utils/error';
import { ConfirmationModal } from '@components/common/ConfirmationModal';
import { ScheduledPrefillContainerModal } from './ScheduledPrefillContainerModal';
import { ScheduledPrefillActivityModal } from './ScheduledPrefillActivityModal';
import { ScheduledPrefillSharedSettingsModal } from './ScheduledPrefillSharedSettingsModal';
import { PersistentLoginHost } from './PersistentLoginHost';
import { useScheduledPrefillContainers } from './useScheduledPrefillContainers';
import { uniqueScheduleName } from './scheduleNames';
import { createUuid } from '@utils/uuid';

interface ScheduledPrefillScheduleDetailProps {
  disabled?: boolean;

  dimmed?: boolean;

  onRunNow: () => void;
  runNowLoading: boolean;
  runNowDisabled: boolean;

  onRunService: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => void;
  isRunServicePending: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => boolean;

  runServiceDisabled: boolean;
}

interface ScheduledPrefillServiceScheduleRowProps {
  serviceKey: ScheduledPrefillServiceKey;

  serviceId: ScheduledPrefillServiceId;
  scheduleId: string;
  label: string;
  enabled: boolean;
  containerRunning: boolean;

  loginState: ScheduledPrefillRowLoginState | null;
  intervalHours: number;

  customSchedule: CustomSchedule | null;

  nextTiming: string;

  nextRunUtc: string | null;
  lastRunUtc: string | null;
  disabled: boolean;
  runPending: boolean;
  runDisabled: boolean;

  isRunning: boolean;

  enablePending: boolean;
  onRun: (serviceId: ScheduledPrefillServiceId, scheduleId: string) => void;
  onContainer: (serviceKey: ScheduledPrefillServiceKey) => void;
  onAdd: (serviceKey: ScheduledPrefillServiceKey) => void;
  onDuplicate: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onDelete: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  triggerRef: (node: HTMLButtonElement | null) => void;
  onOpen: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;

  onToggleEnabled: (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => void;
  onIntervalChange: (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string,
    hours: number
  ) => void;
  onCustomScheduleChange: (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string,
    schedule: CustomSchedule
  ) => void;
}

function ScheduledPrefillServiceScheduleRow({
  serviceKey,
  serviceId,
  scheduleId,
  label,
  enabled,
  containerRunning,
  loginState,
  intervalHours,
  customSchedule,
  nextTiming,
  nextRunUtc,
  lastRunUtc,
  disabled,
  runPending,
  runDisabled,
  isRunning,
  enablePending,
  onRun,
  onOpen,
  onContainer,
  onAdd,
  onDuplicate,
  onDelete,
  triggerRef,
  onToggleEnabled,
  onIntervalChange,
  onCustomScheduleChange
}: ScheduledPrefillServiceScheduleRowProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const nextRunDate = useFormattedDateTime(nextRunUtc);
  const platformUi = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
  const ServiceIcon = platformUi.icon;
  const [actionsOpen, setActionsOpen] = useState(false);
  const actionsId = useId();
  const buttonRef = useRef<HTMLButtonElement | null>(null);
  const invoke = (action: () => void) => {
    if (actionsDisabled) return;
    setActionsOpen(false);
    buttonRef.current?.focus();
    action();
  };

  const actionsDisabled = disabled || enablePending;

  return (
    <div role="row" className={`scheduled-prefill-schedule-table__row ${platformUi.rowClassName}`}>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--service"
      >
        <span
          className="icon-box scheduled-prefill-schedule-table__service-icon"
          aria-hidden="true"
        >
          <ServiceIcon size={18} />
        </span>
        <span className="scheduled-prefill-schedule-table__service-name">{label}</span>
        <span className="scheduled-prefill-schedule-table__service-status">
          <span className="scheduled-prefill-schedule-table__status-item">
            <StatusDot
              tone={enabled ? 'running' : 'error'}
              label={
                enabled
                  ? t(`${baseKey}.platforms.status.enabled`)
                  : t(`${baseKey}.platforms.status.disabled`)
              }
            />
            {enabled
              ? t(`${baseKey}.platforms.status.enabled`)
              : t(`${baseKey}.platforms.status.disabled`)}
          </span>
          <span className="scheduled-prefill-schedule-table__status-item">
            <StatusDot
              tone={containerRunning ? 'running' : 'error'}
              label={
                containerRunning
                  ? t('prefill.persistent.states.running')
                  : t('prefill.persistent.states.stopped')
              }
            />
            {t(`${baseKey}.platforms.status.containerShort`)}:{' '}
            {containerRunning
              ? t('prefill.persistent.states.running')
              : t('prefill.persistent.states.stopped')}
          </span>
          {loginState !== null && (
            <span className="scheduled-prefill-schedule-table__status-item">
              <StatusDot
                tone={loginState === 'loggedIn' ? 'running' : 'warning'}
                label={
                  loginState === 'loggedIn'
                    ? t(`${baseKey}.platforms.status.loggedIn`)
                    : t(`${baseKey}.platforms.status.loginRequired`)
                }
              />
              {loginState === 'loggedIn'
                ? t(`${baseKey}.platforms.status.loggedIn`)
                : t(`${baseKey}.platforms.status.loginRequired`)}
            </span>
          )}
        </span>
      </div>
      <div role="cell" className="scheduled-prefill-schedule-table__cell">
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.nextRun')}
        </span>
        <span className="scheduled-prefill-schedule-table__value tabular-nums">{nextTiming}</span>
        {nextRunUtc && (
          <span className="scheduled-prefill-schedule-table__date tabular-nums">{nextRunDate}</span>
        )}
      </div>
      <div role="cell" className="scheduled-prefill-schedule-table__cell">
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.lastRun')}
        </span>
        <span className="scheduled-prefill-schedule-table__value tabular-nums">
          {formatLastRun(lastRunUtc, t)}
        </span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--interval"
      >
        <span
          className="caps-label schedule-timing-label scheduled-prefill-schedule-table__cell-label"
          aria-hidden="true"
        >
          {t('management.schedules.runEvery')}
        </span>
        <ScheduleIntervalPicker
          intervalHours={intervalHours}
          isDisabled={disabled || enablePending || !enabled}
          onChange={(hours) => onIntervalChange(serviceKey, scheduleId, hours)}
          customSchedule={customSchedule}
          onCustomScheduleChange={(schedule) =>
            onCustomScheduleChange(serviceKey, scheduleId, schedule)
          }
        />
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--action"
      >
        <ActionMenu
          isOpen={actionsOpen}
          onClose={() => setActionsOpen(false)}
          align="right"
          width="w-72"
          className="scheduled-prefill-action-menu"
          id={actionsId}
          aria-label={t('management.actions.menuLabel')}
          trigger={
            <Button
              ref={(node) => {
                buttonRef.current = node;
                triggerRef(node);
              }}
              type="button"
              variant="menu"
              size="md"
              open={actionsOpen}
              className="w-full"
              disabled={actionsDisabled}
              onClick={() => setActionsOpen((open) => !open)}
              aria-expanded={actionsOpen}
              aria-controls={actionsId}
              rightSection={<ChevronDown size={16} aria-hidden="true" />}
            >
              {t('management.actions.menuLabel')}
            </Button>
          }
        >
          <ActionMenuGroup label={t(`${baseKey}.records.label`)}>
            <ActionMenuItem
              onClick={() => invoke(() => onOpen(serviceKey, scheduleId))}
              disabled={actionsDisabled}
            >
              {t(`${baseKey}.records.edit`)}
            </ActionMenuItem>
            <ActionMenuItem
              onClick={() => invoke(() => onRun(serviceId, scheduleId))}
              disabled={actionsDisabled || runDisabled || runPending || isRunning || !enabled}
            >
              {runPending && <LoadingSpinner inline size="xs" />}
              {t(`${baseKey}.records.run`)}
            </ActionMenuItem>
            <ActionMenuItem
              onClick={() => invoke(() => onToggleEnabled(serviceKey, scheduleId))}
              disabled={actionsDisabled}
            >
              {t(`${baseKey}.records.${enabled ? 'disable' : 'enable'}`)}
            </ActionMenuItem>
            <ActionMenuItem
              onClick={() => invoke(() => onDuplicate(serviceKey, scheduleId))}
              disabled={actionsDisabled}
            >
              {t(`${baseKey}.records.duplicate`)}
            </ActionMenuItem>
          </ActionMenuGroup>
          <ActionMenuDivider semantic />
          <ActionMenuGroup label={t(`${baseKey}.services.${serviceKey}`)}>
            <ActionMenuItem
              onClick={() => invoke(() => onAdd(serviceKey))}
              disabled={actionsDisabled}
            >
              {t(`${baseKey}.records.menuAdd`, {
                service: t(`${baseKey}.services.${serviceKey}`)
              })}
            </ActionMenuItem>
            <ActionMenuItem
              onClick={() => invoke(() => onContainer(serviceKey))}
              disabled={actionsDisabled}
            >
              {t(`${baseKey}.records.manageContainer`)}
            </ActionMenuItem>
          </ActionMenuGroup>
          <ActionMenuDivider semantic />
          <ActionMenuDangerItem
            onClick={() => invoke(() => onDelete(serviceKey, scheduleId))}
            disabled={actionsDisabled}
          >
            {t(`${baseKey}.records.delete`)}
          </ActionMenuDangerItem>
        </ActionMenu>
      </div>
    </div>
  );
}

export function ScheduledPrefillScheduleDetail({
  disabled = false,
  dimmed = false,
  onRunNow,
  runNowLoading,
  runNowDisabled,
  onRunService,
  isRunServicePending,
  runServiceDisabled
}: ScheduledPrefillScheduleDetailProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const { on, off, isConnected } = useSignalR();
  const [config, setConfig] = useState<ScheduledPrefillConfigDto | null>(null);
  const records = useRef(new Map<string, ScheduledPrefillSchedule>());
  const [schedule, setSchedule] = useState<ScheduledPrefillServiceScheduleDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [patches, setPatches] = useState<Record<string, Partial<ScheduledPrefillSchedule>>>({});
  const pending = useRef(new Set<string>());
  const [pendingKeys, setPendingKeys] = useState<string[]>([]);
  const [modalRecord, setModalRecord] = useState<ScheduledPrefillEditTarget | null>(null);
  const [containerService, setContainerService] = useState<ScheduledPrefillServiceKey | null>(null);
  const [activityOpen, setActivityOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [actionsOpen, setActionsOpen] = useState(false);
  const actionsId = useId();
  const [deleteTarget, setDeleteTarget] = useState<{
    serviceKey: ScheduledPrefillServiceKey;
    scheduleId: string;
    name: string;
  } | null>(null);
  const opening = useRef(0);
  const actionsRef = useRef<HTMLButtonElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLButtonElement>());
  const emptyRefs = useRef(new Map<ScheduledPrefillServiceKey, HTMLButtonElement>());
  const containers = useScheduledPrefillContainers(containerService);
  const revision = useRef(0);
  const request = useRef<{
    again: boolean;
    controller: AbortController;
    promise: Promise<void>;
  } | null>(null);
  const [now, setNow] = useState(Date.now);
  const refreshSchedule = useCallback(() => {
    if (request.current) {
      request.current.again = true;
      return request.current.promise;
    }
    const read = { again: false, controller: new AbortController(), promise: Promise.resolve() };
    request.current = read;
    read.promise = (async () => {
      try {
        do {
          read.again = false;
          const version = revision.current;
          try {
            const result = await ApiService.getScheduledPrefillSchedule(read.controller.signal);
            if (request.current !== read || read.controller.signal.aborted) return;
            if (version !== revision.current) {
              read.again = true;
              continue;
            }
            setSchedule(result);
            setLoading(false);
            setError(null);
          } catch (failure: unknown) {
            if (request.current !== read || read.controller.signal.aborted) return;
            if (!isAbortError(failure)) setError(getErrorMessage(failure));
          }
        } while (read.again);
      } finally {
        if (request.current === read) request.current = null;
      }
    })();
    return read.promise;
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    const version = revision.current;
    void ApiService.getScheduledPrefillConfig(controller.signal)
      .then((result) => {
        if (!controller.signal.aborted && version === revision.current) {
          setConfig(result);
          for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
            for (const record of result[serviceKey].schedules)
              records.current.set(`${serviceKey}:${record.id}`, record);
          }
        }
      })
      .catch((failure: unknown) => {
        if (!controller.signal.aborted && !isAbortError(failure))
          setError(getErrorMessage(failure));
      });
    void refreshSchedule();
    return () => {
      controller.abort();
      request.current?.controller.abort();
      request.current = null;
    };
  }, [refreshSchedule]);
  useEffect(() => {
    const refresh = () => {
      void refreshSchedule();
    };
    on('SchedulesUpdated', refresh);
    on('ScheduledPrefillStarted', refresh);
    on('ScheduledPrefillCompleted', refresh);
    return () => {
      off('SchedulesUpdated', refresh);
      off('ScheduledPrefillStarted', refresh);
      off('ScheduledPrefillCompleted', refresh);
    };
  }, [on, off, refreshSchedule]);
  useReconnectRefetch(isConnected, () => {
    void refreshSchedule();
  });
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);
  const confirmRecord = useCallback(
    (
      serviceKey: ScheduledPrefillServiceKey,
      record: ScheduledPrefillSchedule,
      committed = false
    ) => {
      records.current.set(`${serviceKey}:${record.id}`, record);
      setConfig((current) =>
        current
          ? {
              ...current,
              [serviceKey]: {
                ...current[serviceKey],
                schedules: current[serviceKey].schedules.some((item) => item.id === record.id)
                  ? current[serviceKey].schedules.map((item) =>
                      item.id === record.id ? record : item
                    )
                  : [...current[serviceKey].schedules, record]
              }
            }
          : current
      );
      if (committed) {
        revision.current += 1;
        setSchedule((current) => {
          const found = current.some(
            (item) =>
              item.serviceId === getPersistentServiceId(serviceKey) && item.scheduleId === record.id
          );
          const patch = {
            name: record.name,
            enabled: record.enabled,
            intervalHours: record.intervalHours,
            customSchedule: record.customSchedule
          };
          return found
            ? current.map((item) =>
                item.serviceId === getPersistentServiceId(serviceKey) &&
                item.scheduleId === record.id
                  ? { ...item, ...patch }
                  : item
              )
            : [
                ...current,
                {
                  ...patch,
                  serviceId: getPersistentServiceId(serviceKey),
                  scheduleId: record.id,
                  isRunning: false,
                  operationId: null,
                  lastRunUtc: null,
                  nextRunUtc: null
                }
              ];
        });
        void refreshSchedule();
      }
    },
    [refreshSchedule]
  );
  const saveServiceConfig = async (
    serviceKey: ScheduledPrefillServiceKey,
    scheduleId: string,
    patch: Partial<Pick<ScheduledPrefillSchedule, 'enabled' | 'intervalHours' | 'customSchedule'>>
  ) => {
    const key = `${serviceKey}:${scheduleId}`;
    if (pending.current.has(key) || pending.current.has('all') || disabled) return;
    const row = schedule.find(
      (item) =>
        item.scheduleId === scheduleId && item.serviceId === getPersistentServiceId(serviceKey)
    );
    if (!row) return;
    pending.current.add(key);
    setPendingKeys([...pending.current]);
    setPatches((current) => ({ ...current, [key]: patch }));
    setError(null);
    try {
      const result =
        patch.enabled !== undefined
          ? await ApiService.setScheduledPrefillScheduleEnabled(
              row.serviceId,
              scheduleId,
              patch.enabled
            )
          : await ApiService.setScheduledPrefillScheduleTiming(
              row.serviceId,
              scheduleId,
              patch.intervalHours ?? row.intervalHours,
              patch.customSchedule === undefined
                ? (row.customSchedule ?? null)
                : patch.customSchedule
            );
      const record = result[serviceKey].schedules.find((item) => item.id === scheduleId);
      if (record) confirmRecord(serviceKey, record, true);
    } catch (failure: unknown) {
      setError(getErrorMessage(failure));
    } finally {
      setPatches((current) => {
        const next = { ...current };
        delete next[key];
        return next;
      });
      pending.current.delete(key);
      setPendingKeys([...pending.current]);
    }
  };
  const handleOpenSchedule = (serviceKey: ScheduledPrefillServiceKey, scheduleId: string) => {
    const row = schedule.find(
      (item) =>
        item.serviceId === getPersistentServiceId(serviceKey) && item.scheduleId === scheduleId
    );
    if (!row) {
      setError(t(`${baseKey}.records.missing`));
      return;
    }
    opening.current += 1;
    setModalRecord({
      opening: opening.current,
      serviceKey,
      scheduleId,
      create: false,
      name: row.name,
      schedule: records.current.get(`${serviceKey}:${scheduleId}`) ?? null
    });
  };
  const createDraft = async (serviceKey: ScheduledPrefillServiceKey, scheduleId?: string) => {
    const attempt = ++opening.current;
    try {
      const result = await ApiService.getScheduledPrefillConfig();
      if (opening.current !== attempt) return;
      const records = result[serviceKey].schedules;
      const source = scheduleId ? records.find((item) => item.id === scheduleId) : records[0];
      if (scheduleId && !source) {
        setError(t(`${baseKey}.records.missing`));
        return;
      }
      const id = createUuid();
      const draft: ScheduledPrefillSchedule = {
        intervalHours: 24,
        customSchedule: null,
        preset: 'All',
        selectedAppIds: [],
        topCount: null,
        operatingSystems: serviceKey === 'steam' ? ['Windows'] : [],
        force: false,
        maxConcurrency: { mode: 'Auto' },
        notificationMode: 'all',
        notificationDisplayMode: 'full',
        ...source,
        id,
        name: uniqueScheduleName(
          scheduleId && source
            ? `${source.name} ${t(`${baseKey}.records.copySuffix`)}`
            : t(`${baseKey}.records.newName`),
          records.map((item) => item.name)
        ),
        enabled: scheduleId ? source!.enabled : false
      };
      setModalRecord({
        opening: attempt,
        serviceKey,
        scheduleId: id,
        create: true,
        name: draft.name,
        schedule: draft
      });
    } catch (failure: unknown) {
      if (opening.current === attempt) setError(getErrorMessage(failure));
    }
  };
  const bulk = async (enabled: boolean) => {
    if (pending.current.size > 0 || disabled) return;
    pending.current.add('all');
    setPendingKeys([...pending.current]);
    try {
      const result = await ApiService.setScheduledPrefillSchedulesEnabled(enabled);
      revision.current += 1;
      for (const serviceKey of SCHEDULED_PREFILL_SERVICE_RUN_ORDER) {
        for (const saved of result[serviceKey].schedules) {
          const key = `${serviceKey}:${saved.id}`;
          const record = records.current.get(key);
          records.current.set(key, record ? { ...record, enabled: saved.enabled } : saved);
        }
      }
      setSchedule((current) =>
        current.map((row) => {
          const serviceKey = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[row.serviceId];
          const saved = result[serviceKey].schedules.find((item) => item.id === row.scheduleId);
          return saved ? { ...row, enabled: saved.enabled } : row;
        })
      );
      setConfig((current) =>
        current
          ? {
              ...current,
              ...Object.fromEntries(
                SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((key) => [
                  key,
                  {
                    ...current[key],
                    schedules: current[key].schedules.map((record) => {
                      const saved = result[key].schedules.find((item) => item.id === record.id);
                      return saved ? { ...record, enabled: saved.enabled } : record;
                    })
                  }
                ])
              )
            }
          : current
      );
      void refreshSchedule();
    } catch (failure: unknown) {
      setError(getErrorMessage(failure));
    } finally {
      pending.current.delete('all');
      setPendingKeys([...pending.current]);
    }
  };
  const remove = async () => {
    if (!deleteTarget) return;
    const target = deleteTarget;
    const key = `${target.serviceKey}:${target.scheduleId}`;
    if (pending.current.has(key)) return;
    pending.current.add(key);
    setPendingKeys([...pending.current]);
    try {
      await ApiService.deleteScheduledPrefillSchedule(
        getPersistentServiceId(target.serviceKey),
        target.scheduleId
      );
      records.current.delete(key);
      revision.current += 1;
      setConfig((current) =>
        current
          ? {
              ...current,
              [target.serviceKey]: {
                ...current[target.serviceKey],
                schedules: current[target.serviceKey].schedules.filter(
                  (item) => item.id !== target.scheduleId
                )
              }
            }
          : current
      );
      setSchedule((current) =>
        current.filter(
          (item) =>
            !(
              item.serviceId === getPersistentServiceId(target.serviceKey) &&
              item.scheduleId === target.scheduleId
            )
        )
      );
      setDeleteTarget(null);
      void refreshSchedule();
      const keys = [...rowRefs.current.keys()];
      const index = keys.indexOf(key);
      const next = keys[index + 1] ?? keys[index - 1];
      setTimeout(
        () =>
          (
            rowRefs.current.get(next) ??
            emptyRefs.current.get(target.serviceKey) ??
            actionsRef.current
          )?.focus(),
        250
      );
    } catch (failure: unknown) {
      setError(getErrorMessage(failure));
      setDeleteTarget(null);
    } finally {
      pending.current.delete(key);
      setPendingKeys([...pending.current]);
    }
  };
  const formatTiming = useCallback(
    (item: ScheduledPrefillServiceScheduleDto): string => {
      const hasCustomSchedule = !!item.customSchedule;
      if (!hasCustomSchedule) {
        if (item.intervalHours === 0) {
          return t(`${baseKey}.nextRunSummary.paused`);
        }
        if (item.intervalHours === -1) {
          return t(`${baseKey}.nextRunSummary.startupOnly`);
        }
      }
      if (!item.nextRunUtc) {
        return hasCustomSchedule
          ? t(`${baseKey}.nextRunSummary.customSchedule`)
          : t(`${baseKey}.nextRunSummary.soon`);
      }

      const diffMs = new Date(item.nextRunUtc).getTime() - now;
      if (diffMs <= 0) {
        return t(`${baseKey}.nextRunSummary.soon`);
      }

      const diffMinutes = Math.floor(diffMs / 60000);
      if (diffMinutes < 60) {
        return t(`${baseKey}.nextRunSummary.inMinutes`, { count: Math.max(1, diffMinutes) });
      }
      const diffHours = Math.floor(diffMinutes / 60);
      if (diffHours < 24) {
        return t(`${baseKey}.nextRunSummary.inHours`, { count: diffHours });
      }
      const diffDays = Math.floor(diffHours / 24);
      return t(`${baseKey}.nextRunSummary.inDays`, { count: diffDays });
    },
    [baseKey, now, t]
  );

  const rows = schedule.map((item) => {
    const key = SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY[item.serviceId];
    return { ...item, ...patches[`${key}:${item.scheduleId}`], key };
  });
  const backendUpdateRequired = config !== null && config.version < 6;
  const tableDisabled = disabled || backendUpdateRequired || pendingKeys.includes('all');
  const globalAction = (action: () => void) => {
    if (tableDisabled) return;
    opening.current += 1;
    setActionsOpen(false);
    actionsRef.current?.focus();
    action();
  };
  const loginTarget = containers.persistentLoginTarget;
  return (
    <>
      <div className="scheduled-prefill-card-summary">
        <div className="scheduled-prefill-card-summary__toolbar">
          {loading ? (
            <LoadingSpinner inline size="sm" />
          ) : (
            <p className={dimmed ? 'schedule-card-disabled' : ''}>
              {t(`${baseKey}.summary`, {
                enabled: rows.filter((row) => row.enabled).length,
                total: rows.length
              })}
            </p>
          )}
          <div className="scheduled-prefill-card-summary__actions">
            <Button
              variant="filled"
              color="run"
              size="md"
              onClick={onRunNow}
              disabled={runNowDisabled || backendUpdateRequired}
              loading={runNowLoading}
            >
              {t('management.schedules.runNow')}
            </Button>
            <ActionMenu
              isOpen={actionsOpen}
              onClose={() => setActionsOpen(false)}
              align="right"
              width="w-64"
              className="scheduled-prefill-action-menu"
              id={actionsId}
              aria-label={t('management.actions.menuLabel')}
              trigger={
                <Button
                  ref={actionsRef}
                  variant="menu"
                  open={actionsOpen}
                  disabled={tableDisabled}
                  onClick={() => setActionsOpen((value) => !value)}
                  rightSection={<ChevronDown size={16} />}
                  aria-expanded={actionsOpen}
                  aria-controls={actionsId}
                >
                  {t('management.actions.menuLabel')}
                </Button>
              }
            >
              <ActionMenuGroup>
                <ActionMenuItem
                  disabled={tableDisabled}
                  onClick={() => globalAction(() => setActivityOpen(true))}
                >
                  {t(`${baseKey}.actions.viewActivity`)}
                </ActionMenuItem>
                <ActionMenuItem
                  disabled={tableDisabled}
                  onClick={() => globalAction(() => setSettingsOpen(true))}
                >
                  {t(`${baseKey}.actions.sharedSettings`)}
                </ActionMenuItem>
              </ActionMenuGroup>
              <ActionMenuDivider semantic />
              <ActionMenuGroup label={t(`${baseKey}.bulkToggle.label`)}>
                <ActionMenuItem
                  disabled={tableDisabled || pendingKeys.length > 0}
                  onClick={() => globalAction(() => void bulk(true))}
                >
                  {t(`${baseKey}.bulkToggle.enableAll`)}
                </ActionMenuItem>
                <ActionMenuItem
                  disabled={tableDisabled || pendingKeys.length > 0}
                  onClick={() => globalAction(() => void bulk(false))}
                >
                  {t(`${baseKey}.bulkToggle.disableAll`)}
                </ActionMenuItem>
              </ActionMenuGroup>
            </ActionMenu>
          </div>
        </div>
        {error && (
          <p role="alert" className="scheduled-prefill-card-summary__error">
            {t(`${baseKey}.summaryError`, { error })}
          </p>
        )}
        {backendUpdateRequired && (
          <p className="scheduled-prefill-card-summary__error">
            {t(`${baseKey}.backendUpdateRequired`)}
          </p>
        )}
        {!loading && (
          <div
            role="table"
            aria-label={t(`${baseKey}.servicesTitle`)}
            className="scheduled-prefill-schedule-table"
          >
            <div role="row" className="scheduled-prefill-schedule-table__head caps-label">
              <span role="columnheader">{t(`${baseKey}.service`)}</span>
              <span role="columnheader">{t('management.schedules.nextRun')}</span>
              <span role="columnheader">{t('management.schedules.lastRun')}</span>
              <span role="columnheader">{t('management.schedules.runEvery')}</span>
              <span role="columnheader">{t('management.actions.menuLabel')}</span>
            </div>
            {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
              const serviceRows = rows.filter((row) => row.key === serviceKey);
              const container = containers.containersByServiceKey.get(serviceKey);
              if (serviceRows.length === 0)
                return (
                  <div
                    role="row"
                    className="scheduled-prefill-schedule-table__empty"
                    key={serviceKey}
                  >
                    <span role="cell">
                      {t(`${baseKey}.records.empty`, {
                        service: t(`${baseKey}.services.${serviceKey}`)
                      })}
                    </span>
                    <Button
                      ref={(node) => {
                        if (node) emptyRefs.current.set(serviceKey, node);
                        else emptyRefs.current.delete(serviceKey);
                      }}
                      disabled={tableDisabled}
                      onClick={() => void createDraft(serviceKey)}
                    >
                      {t(`${baseKey}.records.addForService`, {
                        service: t(`${baseKey}.services.${serviceKey}`)
                      })}
                    </Button>
                  </div>
                );
              return serviceRows.map((row) => {
                const rowId = `${serviceKey}:${row.scheduleId}`;
                return (
                  <ScheduledPrefillServiceScheduleRow
                    key={rowId}
                    serviceKey={serviceKey}
                    serviceId={row.serviceId}
                    scheduleId={row.scheduleId}
                    label={`${t(`${baseKey}.services.${serviceKey}`)} · ${row.name}`}
                    enabled={row.enabled}
                    containerRunning={container?.isRunning === true}
                    loginState={
                      isScheduledPrefillAccountService(serviceKey)
                        ? container?.isRunning &&
                          container.isAuthenticated &&
                          !container.needsRelogin
                          ? 'loggedIn'
                          : 'loginRequired'
                        : null
                    }
                    intervalHours={row.intervalHours}
                    customSchedule={row.customSchedule ?? null}
                    nextTiming={row.enabled ? formatTiming(row) : ''}
                    nextRunUtc={
                      row.enabled && row.nextRunUtc && new Date(row.nextRunUtc).getTime() > now
                        ? row.nextRunUtc
                        : null
                    }
                    lastRunUtc={row.lastRunUtc}
                    disabled={tableDisabled}
                    runPending={isRunServicePending(row.serviceId, row.scheduleId)}
                    runDisabled={runServiceDisabled || row.isRunning}
                    isRunning={row.isRunning}
                    enablePending={pendingKeys.includes(rowId)}
                    onRun={onRunService}
                    onOpen={handleOpenSchedule}
                    onContainer={(key) => {
                      opening.current += 1;
                      setContainerService(key);
                    }}
                    onAdd={(key) => void createDraft(key)}
                    onDuplicate={(key, id) => void createDraft(key, id)}
                    onDelete={(key, id) =>
                      setDeleteTarget({ serviceKey: key, scheduleId: id, name: row.name })
                    }
                    triggerRef={(node) => {
                      if (node) rowRefs.current.set(rowId, node);
                      else rowRefs.current.delete(rowId);
                    }}
                    onToggleEnabled={(key, id) =>
                      void saveServiceConfig(key, id, { enabled: !row.enabled })
                    }
                    onIntervalChange={(key, id, hours) =>
                      void saveServiceConfig(key, id, {
                        intervalHours: hours,
                        customSchedule: null
                      })
                    }
                    onCustomScheduleChange={(key, id, customSchedule) =>
                      void saveServiceConfig(key, id, { customSchedule })
                    }
                  />
                );
              });
            })}
          </div>
        )}
        {!loading && !rows.some((row) => row.enabled) && (
          <p className="scheduled-prefill-card-summary__warning">
            {t(`${baseKey}.zeroEnabledWarning`)}
          </p>
        )}
      </div>
      <ScheduledPrefillConfigModal
        target={modalRecord}
        identity={containers.privateAvailabilityIdentity}
        container={
          modalRecord ? containers.containersByServiceKey.get(modalRecord.serviceKey) : undefined
        }
        onClose={() => {
          opening.current += 1;
          setModalRecord(null);
        }}
        onLoaded={confirmRecord}
        onSaved={(serviceKey, record) => confirmRecord(serviceKey, record, true)}
      />
      <ScheduledPrefillContainerModal
        serviceKey={containerService}
        containers={containers}
        disabled={disabled}
        onClose={() => setContainerService(null)}
      />
      <ScheduledPrefillActivityModal
        opened={activityOpen}
        containers={containers}
        disabled={disabled}
        onClose={() => setActivityOpen(false)}
      />
      <ScheduledPrefillSharedSettingsModal
        opened={settingsOpen}
        containers={containers}
        onClose={() => setSettingsOpen(false)}
      />
      {loginTarget && (
        <PersistentLoginHost
          serviceKey={loginTarget}
          isRunning={containers.containersByServiceKey.get(loginTarget)?.isRunning === true}
          isAuthenticated={
            containers.containersByServiceKey.get(loginTarget)?.isAuthenticated === true
          }
          onAuthenticated={() => {
            void containers.loadPersistentContainers();
          }}
          onDismiss={containers.handleDismissPersistentLogin}
        />
      )}
      <ConfirmationModal
        opened={deleteTarget !== null}
        onClose={() => setDeleteTarget(null)}
        onConfirm={() => void remove()}
        title={t(`${baseKey}.records.deleteTitle`, {
          service: deleteTarget && t(`${baseKey}.services.${deleteTarget.serviceKey}`),
          name: deleteTarget?.name
        })}
        confirmLabel={t(`${baseKey}.records.delete`)}
        confirmColor="red"
        loading={
          deleteTarget !== null &&
          pendingKeys.includes(`${deleteTarget.serviceKey}:${deleteTarget.scheduleId}`)
        }
      >
        <p>{t(`${baseKey}.records.deleteBody`)}</p>
      </ConfirmationModal>
    </>
  );
}
