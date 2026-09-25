import { useCallback, useEffect, useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@components/ui/Button';
import { Tooltip } from '@components/ui/Tooltip';
import {
  ActionMenu,
  ActionMenuDangerItem,
  ActionMenuDivider,
  ActionMenuGroup,
  ActionMenuItem
} from '@components/ui/ActionMenu';
import { RowActionsMenu } from '@components/ui/RowActionsMenu';
import LoadingSpinner from '@components/common/LoadingSpinner';
import { ChevronDown } from 'lucide-react';
import StatusDot from '@components/common/StatusDot';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import ApiService from '@services/api.service';
import {
  SCHEDULED_PREFILL_BUTTON_SIZE,
  SCHEDULED_PREFILL_PLATFORM_TO_SERVICE_KEY,
  SCHEDULED_PREFILL_SERVICE_RUN_ORDER
} from './constants';
import ScheduleIntervalPicker from '../ScheduleIntervalPicker';
import { getScheduleIntervalOptions } from '../constants';
import { formatIntervalLabel, formatLastRun } from '../scheduleFormatting';
import type { CustomSchedule } from '../custom-schedule/types';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { ScheduledPrefillConfigModal } from './ScheduledPrefillConfigModal';
import {
  getPersistentServiceId,
  getScheduledPrefillServiceStatus,
  getScheduledPrefillStatusFact,
  SCHEDULED_PREFILL_PLATFORM_UI
} from './scheduledPrefillPlatformUi';
import {
  getPersistentLoginFailure,
  getPersistentLoginState,
  usePersistentLoginStoreState
} from './persistentLoginStore';
import type {
  ScheduledPrefillConfigDto,
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

interface ScheduledPrefillServiceRowProps {
  serviceKey: ScheduledPrefillServiceKey;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
  disabled: boolean;
  onOpen: (serviceKey: ScheduledPrefillServiceKey) => void;
}

function ScheduledPrefillServiceRow({
  serviceKey,
  containers,
  disabled,
  onOpen
}: ScheduledPrefillServiceRowProps) {
  const { t } = useTranslation();
  const baseKey = 'management.schedules.services.scheduledPrefill.config';
  const platformUi = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey];
  const ServiceIcon = platformUi.icon;
  const container = containers.containersByServiceKey.get(serviceKey);
  const loginError = getPersistentLoginFailure(
    usePersistentLoginStoreState(getPersistentServiceId(serviceKey))
  );
  const reloginDate = useFormattedDateTime(container?.authExpiresAtUtc);
  const action = containers.actions[serviceKey] ?? null;
  const status = getScheduledPrefillServiceStatus(serviceKey, {
    container,
    listLoaded: containers.persistentContainers !== null,
    listFailed: containers.persistentError !== null,
    action,
    authenticating: containers.authenticatingServiceKeys.some(
      (accountKey) => accountKey === serviceKey
    ),
    loginError
  });
  const containerFact = getScheduledPrefillStatusFact(status.container, container, t);
  const accountFact = getScheduledPrefillStatusFact(status.account, container, t);
  // Start is the only action a row runs; Stop, Log out and Log in errors stay in the dialog.
  const startError =
    status.next === 'start' && containers.errorActions[serviceKey] === 'start'
      ? containers.errors[serviceKey]
      : undefined;

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
        <span className="scheduled-prefill-schedule-table__service-name">
          {t(`prefill.persistent.services.${serviceKey}`)}
        </span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--container"
      >
        <span className="scheduled-prefill-schedule-table__fact">
          {containerFact.busy && <LoadingSpinner inline size="xs" />}
          {containerFact.tone !== null && (
            <StatusDot tone={containerFact.tone} label={containerFact.label} />
          )}
          {containerFact.label}
        </span>
        {startError && (
          <p role="alert" className="scheduled-prefill-services__error">
            {startError}
          </p>
        )}
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--account"
      >
        <span className="scheduled-prefill-schedule-table__fact">
          {accountFact.busy && <LoadingSpinner inline size="xs" />}
          {accountFact.tone !== null && (
            <StatusDot tone={accountFact.tone} label={accountFact.label} />
          )}
          {accountFact.label}
          {status.account === 'loggedIn' && container && (
            <span className="scheduled-prefill-schedule-table__relogin text-themed-muted tabular-nums">
              {t(`${baseKey}.serviceStatus.reloginBy`, { date: reloginDate })}
            </span>
          )}
        </span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--next"
      >
        {status.next === 'start' ? (
          <Button
            type="button"
            variant="filled"
            color="run"
            size={SCHEDULED_PREFILL_BUTTON_SIZE}
            onClick={() => void containers.handleStartPersistent(serviceKey)}
            disabled={disabled || action !== null}
          >
            {t(`${baseKey}.serviceActions.start`)}
          </Button>
        ) : status.next === 'logIn' ? (
          <Button
            type="button"
            variant="filled"
            color="primary"
            size={SCHEDULED_PREFILL_BUTTON_SIZE}
            onClick={() => onOpen(serviceKey)}
          >
            {t(`${baseKey}.logIn`)}
          </Button>
        ) : (
          <Button
            type="button"
            size={SCHEDULED_PREFILL_BUTTON_SIZE}
            onClick={() => onOpen(serviceKey)}
            disabled={status.container === 'checking' || status.container === 'unknown'}
          >
            {t(`${baseKey}.serviceActions.manage`)}
          </Button>
        )}
      </div>
    </div>
  );
}

interface ScheduledPrefillServiceScheduleRowProps {
  serviceKey: ScheduledPrefillServiceKey;

  serviceId: ScheduledPrefillServiceId;
  scheduleId: string;
  label: string;
  enabled: boolean;
  containers: ReturnType<typeof useScheduledPrefillContainers>;
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
  containers,
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
  const serviceName = t(`prefill.persistent.services.${serviceKey}`);
  const [actionsOpen, setActionsOpen] = useState(false);
  const actionsId = useId();
  const buttonRef = useRef<HTMLButtonElement | null>(null);
  const loginError = getPersistentLoginFailure(
    usePersistentLoginStoreState(getPersistentServiceId(serviceKey))
  );
  const container = containers.containersByServiceKey.get(serviceKey);
  const status = getScheduledPrefillServiceStatus(serviceKey, {
    container,
    listLoaded: containers.persistentContainers !== null,
    listFailed: containers.persistentError !== null,
    action: containers.actions[serviceKey] ?? null,
    authenticating: containers.authenticatingServiceKeys.some(
      (accountKey) => accountKey === serviceKey
    ),
    loginError
  });
  const waitsForLogin =
    status.account === 'loginRequired' ||
    status.account === 'loginExpired' ||
    status.account === 'loginFailed';
  const invoke = (action: () => void) => {
    if (actionsDisabled) return;
    setActionsOpen(false);
    buttonRef.current?.focus();
    action();
  };

  const actionsDisabled = disabled || enablePending;
  const runReason = !enabled
    ? t(`${baseKey}.records.runNeedsEnable`)
    : isRunning
      ? t(`${baseKey}.records.runAlreadyRunning`)
      : null;
  const intervalLabel = (() => {
    if (customSchedule) return t('management.schedules.customSchedule.savedLabel');
    const option = getScheduleIntervalOptions(t).find(
      (item) => item.value === String(intervalHours)
    );
    if (option) return option.label;
    return formatIntervalLabel(intervalHours, t);
  })();
  const timeLine = [
    intervalLabel,
    nextTiming === '' ? null : t(`${baseKey}.scheduleMeta.runs`, { when: nextTiming }),
    lastRunUtc
      ? t(`${baseKey}.scheduleMeta.ran`, { when: formatLastRun(lastRunUtc, t) })
      : t(`${baseKey}.scheduleMeta.neverRan`)
  ]
    .filter((part): part is string => part !== null && part !== '')
    .join(' · ');

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
        <span className="scheduled-prefill-schedule-table__meta">{serviceName}</span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--status"
      >
        <span className="scheduled-prefill-schedule-table__meta">
          {/* Phones show the service name here, on the status line, instead of under the name. */}
          <span className="hidden max-md:inline">{serviceName}</span>
          <span className="scheduled-prefill-schedule-table__fact">
            <StatusDot
              tone={enabled ? 'running' : 'idle'}
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
          {status.container === 'stopped' && (
            <span className="text-themed-warning">{t('prefill.persistent.status.stopped')}</span>
          )}
          {waitsForLogin && (
            <span className="text-themed-warning">
              {t(`${baseKey}.scheduleMeta.waitsForLogin`, { service: serviceName })}
            </span>
          )}
        </span>
        <span className="scheduled-prefill-schedule-table__time-line tabular-nums">{timeLine}</span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--timing"
      >
        <span className="scheduled-prefill-schedule-table__value tabular-nums">{nextTiming}</span>
        {nextRunUtc && (
          <span className="scheduled-prefill-schedule-table__date tabular-nums">{nextRunDate}</span>
        )}
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--timing"
      >
        <span className="scheduled-prefill-schedule-table__value tabular-nums">
          {formatLastRun(lastRunUtc, t)}
        </span>
      </div>
      <div
        role="cell"
        className="scheduled-prefill-schedule-table__cell scheduled-prefill-schedule-table__cell--interval"
      >
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
        <RowActionsMenu
          open={actionsOpen}
          onOpenChange={setActionsOpen}
          width="w-56"
          id={actionsId}
          className="scheduled-prefill-action-menu"
          disabled={actionsDisabled}
          triggerRef={(node: HTMLButtonElement | null) => {
            buttonRef.current = node;
            triggerRef(node);
          }}
          aria-label={t('management.actions.menuLabel')}
        >
          {() => (
            <>
              <ActionMenuItem
                onClick={() => invoke(() => onRun(serviceId, scheduleId))}
                disabled={actionsDisabled || runDisabled || runPending || isRunning || !enabled}
              >
                {runReason === null ? (
                  t(`${baseKey}.records.run`)
                ) : (
                  <span className="flex flex-col items-start min-w-0 whitespace-normal">
                    {t(`${baseKey}.records.run`)}
                    <span className="text-themed-muted">{runReason}</span>
                  </span>
                )}
              </ActionMenuItem>
              <ActionMenuItem
                onClick={() => invoke(() => onOpen(serviceKey, scheduleId))}
                disabled={actionsDisabled}
              >
                {t(`${baseKey}.records.edit`)}
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
              <ActionMenuDivider semantic />
              <ActionMenuDangerItem
                onClick={() => invoke(() => onDelete(serviceKey, scheduleId))}
                disabled={actionsDisabled}
              >
                {t(`${baseKey}.records.delete`)}
              </ActionMenuDangerItem>
            </>
          )}
        </RowActionsMenu>
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
  const { notifyError } = useErrorHandler();
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
  const [loggedInService, setLoggedInService] = useState<ScheduledPrefillServiceKey | null>(null);
  const [activityOpen, setActivityOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [actionsOpen, setActionsOpen] = useState(false);
  const actionsId = useId();
  const [addOpen, setAddOpen] = useState(false);
  const addId = useId();
  const [deleteTarget, setDeleteTarget] = useState<{
    serviceKey: ScheduledPrefillServiceKey;
    scheduleId: string;
    name: string;
  } | null>(null);
  const opening = useRef(0);
  const actionsRef = useRef<HTMLButtonElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLButtonElement>());
  const addTriggerRef = useRef<HTMLButtonElement | null>(null);
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
        // The dialogs and a new draft read the config again, so a failed pre-load changes
        // nothing on screen and is only logged.
        if (!controller.signal.aborted && !isAbortError(failure))
          notifyError(t(`${baseKey}.loadFailed`), failure, {
            silent: true,
            logLabel: '[ScheduledPrefillScheduleDetail] Failed to pre-load schedule records'
          });
      });
    void refreshSchedule();
    return () => {
      controller.abort();
      request.current?.controller.abort();
      request.current = null;
    };
  }, [refreshSchedule, notifyError, t]);
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
      notifyError(t(`${baseKey}.saveFailed`), failure);
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
      notifyError(t(`${baseKey}.records.missing`));
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
      // A new schedule starts from the defaults below; only Duplicate copies a source.
      const source = scheduleId ? records.find((item) => item.id === scheduleId) : undefined;
      if (scheduleId && !source) {
        notifyError(t(`${baseKey}.records.missing`));
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
      if (opening.current === attempt) notifyError(t(`${baseKey}.openFailed`), failure);
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
      notifyError(t(`${baseKey}.bulkUpdateFailed`), failure);
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
      // The last row hands focus to Add schedule, not to the row above it.
      const next = keys[index + 1];
      setTimeout(
        () =>
          (
            (next === undefined ? undefined : rowRefs.current.get(next)) ??
            addTriggerRef.current ??
            actionsRef.current
          )?.focus(),
        250
      );
    } catch (failure: unknown) {
      notifyError(t(`${baseKey}.deleteFailed`), failure);
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
  // The Run Now explanation keys on this, never on runNowDisabled: that is also true for a
  // non-admin and while a run is active or pending, when "enable a schedule" would be wrong. [69]
  const noScheduleEnabled = !loading && !rows.some((row) => row.enabled);
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
            error === null && <LoadingSpinner inline size="sm" />
          ) : (
            <p className={dimmed ? 'schedule-card-disabled' : ''}>
              {t(`${baseKey}.summary`, {
                enabled: rows.filter((row) => row.enabled).length,
                total: rows.length
              })}
            </p>
          )}
          <div className="scheduled-prefill-card-summary__actions">
            <Tooltip
              content={
                noScheduleEnabled
                  ? t('management.schedules.services.scheduledPrefill.runNowNoSchedule')
                  : null
              }
              className="scheduled-prefill-card-summary__run-help"
            >
              <span
                tabIndex={noScheduleEnabled ? 0 : undefined}
                aria-label={
                  noScheduleEnabled
                    ? t('management.schedules.services.scheduledPrefill.runNowNoSchedule')
                    : undefined
                }
              >
                <Button
                  variant="filled"
                  color="run"
                  size="md"
                  onClick={onRunNow}
                  disabled={runNowDisabled || backendUpdateRequired || runNowLoading}
                >
                  {t(`${baseKey}.runAll`)}
                </Button>
              </span>
            </Tooltip>
            <div className="scheduled-prefill-card-summary__add">
              <ActionMenu
                isOpen={addOpen}
                onClose={() => setAddOpen(false)}
                align="right"
                width="w-40"
                className="scheduled-prefill-action-menu"
                id={addId}
                aria-label={t(`${baseKey}.records.addSchedule`)}
                trigger={
                  <Button
                    ref={addTriggerRef}
                    variant="menu"
                    size={SCHEDULED_PREFILL_BUTTON_SIZE}
                    open={addOpen}
                    disabled={tableDisabled || loading}
                    onClick={() => setAddOpen((value) => !value)}
                    rightSection={<ChevronDown size={16} aria-hidden="true" />}
                    aria-expanded={addOpen}
                    aria-controls={addId}
                  >
                    {t(`${baseKey}.records.addSchedule`)}
                  </Button>
                }
              >
                {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => {
                  const ServiceIcon = SCHEDULED_PREFILL_PLATFORM_UI[serviceKey].icon;
                  return (
                    <ActionMenuItem
                      key={serviceKey}
                      icon={<ServiceIcon size={16} />}
                      disabled={tableDisabled}
                      onClick={() => {
                        setAddOpen(false);
                        addTriggerRef.current?.focus();
                        void createDraft(serviceKey);
                      }}
                    >
                      {t(`prefill.persistent.services.${serviceKey}`)}
                    </ActionMenuItem>
                  );
                })}
              </ActionMenu>
            </div>
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
              <ActionMenuItem
                disabled={tableDisabled}
                onClick={() => globalAction(() => setActivityOpen(true))}
              >
                {t(`${baseKey}.actions.viewActivity`)}
              </ActionMenuItem>
              <ActionMenuDivider semantic />
              <ActionMenuGroup label={t(`${baseKey}.actions.allServicesGroup`)}>
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
          <ErrorBlock
            title={t(`${baseKey}.loadFailed`)}
            message={error}
            retryLabel={t('common.retry')}
            onRetry={() => void refreshSchedule()}
          />
        )}
        {backendUpdateRequired && (
          <p className="scheduled-prefill-card-summary__error">
            {t(`${baseKey}.backendUpdateRequired`)}
          </p>
        )}
        {containers.persistentError && (
          <ErrorBlock
            title={t(`${baseKey}.serviceStatus.loadFailed`)}
            message={containers.persistentError}
            retryLabel={t('common.retry')}
            onRetry={() => void containers.loadPersistentContainers()}
          />
        )}
        {/* After an earlier good read the rows keep their last known state, so the line applies
            only before the first read answers. */}
        {containers.persistentContainers === null && containers.persistentError !== null && (
          <p className="scheduled-prefill-persistent-card__hint">
            {t(`${baseKey}.serviceStatus.unknownUntilLoaded`)}
          </p>
        )}
        <div
          role="table"
          aria-label={t(`${baseKey}.servicesTitle`)}
          className="scheduled-prefill-schedule-table scheduled-prefill-schedule-table--services"
        >
          <p className="caps-label scheduled-prefill-schedule-table__legend" aria-hidden="true">
            {[
              t(`${baseKey}.service`),
              t(`${baseKey}.persistentContainers.steps.container`),
              t(`${baseKey}.persistentContainers.steps.account`)
            ].join(' · ')}
          </p>
          <div role="row" className="scheduled-prefill-schedule-table__head caps-label">
            <span role="columnheader">{t(`${baseKey}.service`)}</span>
            <span role="columnheader">{t(`${baseKey}.persistentContainers.steps.container`)}</span>
            <span role="columnheader">{t(`${baseKey}.persistentContainers.steps.account`)}</span>
            <span role="columnheader" />
          </div>
          {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.map((serviceKey) => (
            <ScheduledPrefillServiceRow
              key={serviceKey}
              serviceKey={serviceKey}
              containers={containers}
              disabled={tableDisabled}
              onOpen={(key) => {
                opening.current += 1;
                setContainerService(key);
              }}
            />
          ))}
        </div>
        {!loading && (
          <div
            role="table"
            aria-label={t(`${baseKey}.schedulesTitle`)}
            className="scheduled-prefill-schedule-table"
          >
            <p className="caps-label scheduled-prefill-schedule-table__legend" aria-hidden="true">
              {t(`${baseKey}.schedulesTitle`)}
            </p>
            <div role="row" className="scheduled-prefill-schedule-table__head caps-label">
              <span role="columnheader">{t(`${baseKey}.scheduleColumn`)}</span>
              <span role="columnheader">{t(`${baseKey}.statusColumn`)}</span>
              <span role="columnheader">{t('management.schedules.nextRun')}</span>
              <span role="columnheader">{t('management.schedules.lastRun')}</span>
              <span role="columnheader">{t('management.schedules.runEvery')}</span>
              <span role="columnheader" />
            </div>
            {SCHEDULED_PREFILL_SERVICE_RUN_ORDER.flatMap((serviceKey) =>
              rows
                .filter((row) => row.key === serviceKey)
                .map((row) => {
                  const rowId = `${serviceKey}:${row.scheduleId}`;
                  return (
                    <ScheduledPrefillServiceScheduleRow
                      key={rowId}
                      serviceKey={serviceKey}
                      serviceId={row.serviceId}
                      scheduleId={row.scheduleId}
                      label={row.name}
                      enabled={row.enabled}
                      containers={containers}
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
                })
            )}
          </div>
        )}
        {noScheduleEnabled && (
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
        justLoggedIn={loggedInService !== null && loggedInService === containerService}
        onLogout={() => {
          setLoggedInService(null);
          if (containerService) void containers.handleLogoutPersistent(containerService);
        }}
        onClose={() => {
          setContainerService(null);
          setLoggedInService(null);
        }}
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
            setLoggedInService(loginTarget);
            void containers.loadPersistentContainers();
          }}
          onDismiss={() => {
            // The host also dismisses when the container list reports the login before the
            // prompt does; onAuthenticated never runs on that path.
            if (
              containers.containersByServiceKey.get(loginTarget)?.isAuthenticated === true ||
              getPersistentLoginState(getPersistentServiceId(loginTarget)).authenticated
            )
              setLoggedInService(loginTarget);
            containers.handleDismissPersistentLogin();
          }}
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
        busyLabel={t(`${baseKey}.records.deleting`)}
      >
        <p>{t(`${baseKey}.records.deleteBody`)}</p>
      </ConfirmationModal>
    </>
  );
}
