import React, { useEffect, useState, useCallback, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, ChevronsDownUp, ChevronsUpDown } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { HelpPopover, HelpSection } from '@components/ui/HelpPopover';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { GroupHeading } from '@components/ui/GroupHeading';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionErrorChip, SectionHeaderActions } from '@components/ui/SectionHeaderActions';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import ApiService from '@services/api.service';
import { assertOk } from '@services/apiError';
import { getErrorMessage } from '@utils/error';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import type { DefaultGuestPreferences } from '@hooks/useDefaultGuestPreferences';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import {
  PREFILL_SERVICES,
  prefillServiceRecord,
  type GuestPrefillConfigChangedPayload,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import type { GameServiceId } from '@/types/gameService';
import type { ClockPreferences } from '@/types/userPreferences';
import type { DefaultGuestPreferencesChangedEvent } from '@contexts/SignalRContext/types';
import { TIME_SETTING_VALUES, type TimeSettingValue } from '@contexts/TimezoneContext.types';
import { clockFromTimeSetting, timeSettingFromClock } from '@utils/pendingPreferences';
import { type ThemeOption, durationOptions, refreshRateOptions, showToast } from './types';
import AccessSecurityCard from './AccessSecurityCard';
import PrefillServicePanel from './PrefillServicePanel';
import AppearanceDisplayCard from './AppearanceDisplayCard';
import {
  toGuestPrefillConfig,
  type GuestPrefillConfig,
  type GuestPrefillConfigField,
  type GuestPrefillConfigResponse
} from './guestPrefillConfig';
import { getThreadOptions } from './threadOptions';
import '@components/features/management/managementSectionContent.css';
import './user-settings.css';

interface DefaultGuestPreferencesResponse {
  useLocalTimezone: boolean;
  useUtcTimezone?: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  allowedTimeFormats?: string[];
}

// A prefill change held while its service's read or save is open; `field` names the control that
// shows busy once it is sent.
interface PendingPrefillEdit {
  field: GuestPrefillConfigField;
  edits: Partial<GuestPrefillConfig>;
}

interface GuestConfigurationProps {
  guestDurationHours: number;
  onDurationChange: (duration: number) => void;
  updatingDuration: boolean;
  defaultGuestTheme: string;
  onGuestThemeChange: (themeId: string) => void;
  updatingGuestTheme: boolean;
  defaultGuestRefreshRate: string;
  onGuestRefreshRateChange: (rate: string) => void;
  updatingGuestRefreshRate: boolean;
  guestRefreshRateLocked: boolean;
  onGuestRefreshRateLockChange: (locked: boolean) => void;
  updatingGuestRefreshRateLock: boolean;
  availableThemes: ThemeOption[];
}

const GuestConfiguration: React.FC<GuestConfigurationProps> = ({
  guestDurationHours,
  onDurationChange,
  updatingDuration,
  defaultGuestTheme,
  onGuestThemeChange,
  updatingGuestTheme,
  defaultGuestRefreshRate,
  onGuestRefreshRateChange,
  updatingGuestRefreshRate,
  guestRefreshRateLocked,
  onGuestRefreshRateLockChange,
  updatingGuestRefreshRateLock,
  availableThemes
}) => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
  const { on, off, isConnected } = useSignalR();
  const { authMode } = useAuth();
  const [defaultGuestPreferences, setDefaultGuestPreferences] = useState<DefaultGuestPreferences>({
    useLocalTimezone: false,
    useUtcTimezone: false,
    use24HourFormat: true,
    sharpCorners: false,
    disableTooltips: false,
    showDatasourceLabels: true,
    allowedTimeFormats: [...TIME_SETTING_VALUES]
  });
  const [loadingDefaultPrefs, setLoadingDefaultPrefs] = useState(false);
  const [defaultPrefsError, setDefaultPrefsError] = useState<string | null>(null);
  const [prefillConfigError, setPrefillConfigError] = useState<string | null>(null);
  const defaultPrefsRequestRef = useRef(0);
  const defaultPrefsLoadedRef = useRef(false);
  // Fields written or pushed since the newest reload started keep their screen value when it answers.
  const defaultPrefsChangedRef = useRef(new Set<keyof DefaultGuestPreferences>());
  // Pushes received per field. The server announces a save before it answers it, so a save's reply
  // applies only to fields no push touched while the save was open.
  const defaultPrefsPushesRef = useRef(new Map<keyof DefaultGuestPreferences, number>());
  const prefillConfigRequestRef = useRef(0);
  const [updatingDefaultPref, setUpdatingDefaultPref] = useState<string | null>(null);
  const [updatingAllowedFormats, setUpdatingAllowedFormats] = useState(false);

  // Guest prefill permissions, one entry per service in PREFILL_SERVICES. The load and
  // update calls below are keyed off the same table, so a new service needs no state here.
  const [prefillConfigs, setPrefillConfigs] = useState<Record<GameServiceId, GuestPrefillConfig>>(
    () =>
      prefillServiceRecord<GuestPrefillConfig>(() => ({
        enabledByDefault: false,
        durationHours: 2,
        maxThreadCount: null
      }))
  );
  const [loadingPrefillConfigs, setLoadingPrefillConfigs] = useState<
    Record<GameServiceId, boolean>
  >(() => prefillServiceRecord<boolean>(() => false));
  const [updatingPrefillConfigs, setUpdatingPrefillConfigs] = useState<
    Record<GameServiceId, GuestPrefillConfigField | null>
  >(() => prefillServiceRecord<GuestPrefillConfigField | null>(() => null));
  // The change a service's open save is sending, and a change made while that save or the first
  // read was open, held to send when it ends. The panel shows both on top of the stored values.
  const [sendingPrefillEdits, setSendingPrefillEdits] = useState<
    Record<GameServiceId, Partial<GuestPrefillConfig> | null>
  >(() => prefillServiceRecord<Partial<GuestPrefillConfig> | null>(() => null));
  const [pendingPrefillEdits, setPendingPrefillEdits] = useState<
    Record<GameServiceId, PendingPrefillEdit | null>
  >(() => prefillServiceRecord<PendingPrefillEdit | null>(() => null));

  const [prefillSectionExpanded, setPrefillSectionExpanded] = useState(false);
  useAccordionGroupItem('guest-prefill-services', prefillSectionExpanded, () =>
    setPrefillSectionExpanded((prev) => !prev)
  );
  const [prefillServiceExpanded, setPrefillServiceExpanded] = useState<
    Record<GameServiceId, boolean>
  >(() => prefillServiceRecord<boolean>(() => false));

  const enabledPrefillCount = useMemo(
    () =>
      PREFILL_SERVICES.filter(
        (service: PrefillServiceConfig) => prefillConfigs[service.id].enabledByDefault
      ).length,
    [prefillConfigs]
  );

  const allPrefillServicesExpanded = PREFILL_SERVICES.every(
    (service: PrefillServiceConfig) => prefillServiceExpanded[service.id]
  );

  const togglePrefillService = (serviceId: GameServiceId) => {
    setPrefillServiceExpanded((prev: Record<GameServiceId, boolean>) => ({
      ...prev,
      [serviceId]: !prev[serviceId]
    }));
  };

  const handlePrefillExpandCollapseAll = () => {
    const next = !allPrefillServicesExpanded;
    setPrefillServiceExpanded(prefillServiceRecord<boolean>(() => next));
  };

  const applyDefaultPrefsChange = useCallback((patch: Partial<DefaultGuestPreferences>) => {
    for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
      defaultPrefsChangedRef.current.add(key);
    setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({ ...prev, ...patch }));
  }, []);

  const applyDefaultPrefsPush = useCallback(
    (patch: Partial<DefaultGuestPreferences>) => {
      for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
        defaultPrefsPushesRef.current.set(key, (defaultPrefsPushesRef.current.get(key) ?? 0) + 1);
      applyDefaultPrefsChange(patch);
    },
    [applyDefaultPrefsChange]
  );

  const applyDefaultPrefsReply = useCallback(
    (
      patch: Partial<DefaultGuestPreferences>,
      pushesBefore: ReadonlyMap<keyof DefaultGuestPreferences, number>
    ) => {
      const unpushed: Partial<DefaultGuestPreferences> = {};
      for (const key of Object.keys(patch) as (keyof DefaultGuestPreferences)[])
        if (defaultPrefsPushesRef.current.get(key) === pushesBefore.get(key))
          Object.assign(unpushed, { [key]: patch[key] });
      applyDefaultPrefsChange(unpushed);
    },
    [applyDefaultPrefsChange]
  );

  // Helper to update default time format based on a format value.
  //
  // One request, because the three flags are one clock. Sent as three they commit separately: a
  // sibling that fails, or an admin on another tab writing between two of them, leaves the stored
  // default naming a clock nobody picked, and the next guest session seeds from it.
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const pushesBefore = new Map(defaultPrefsPushesRef.current);
    const response = await fetch(
      '/api/system/default-guest-preferences/clock',
      ApiService.getJsonFetchOptions(clockFromTimeSetting(format), { method: 'PATCH' })
    );

    await assertOk(response);
    // The server normalizes before storing, so the stored clock is read back off the reply rather
    // than assumed from what was sent.
    const { clock } = (await response.json()) as { clock: ClockPreferences };
    applyDefaultPrefsReply(clock, pushesBefore);
  };

  // Get current default time format from the boolean settings behind it.
  const getCurrentDefaultFormat = (): TimeSettingValue =>
    timeSettingFromClock(defaultGuestPreferences);

  const translatedDurationOptions = durationOptions.map(
    (option: { value: string; label: string }) => ({
      ...option,
      label: t(`user.guest.durationOptions.${option.value}`)
    })
  );
  const translatedRefreshRateOptions = refreshRateOptions.map(
    (option: { value: string; label: string }) => ({
      ...option,
      label: t(`user.guest.refreshRates.${option.value}`)
    })
  );
  const prefillDurationOptions = [
    { value: '1', label: t('user.guest.prefillDurationOptions.1') },
    { value: '2', label: t('user.guest.prefillDurationOptions.2') },
    { value: '3', label: t('user.guest.prefillDurationOptions.3') }
  ];
  const maxThreadOptions = getThreadOptions(t);
  const preferenceLabels: Record<string, string> = {
    sharpCorners: t('user.guest.preferences.sharpCorners.label'),
    disableTooltips: t('user.guest.preferences.disableTooltips.label'),
    showDatasourceLabels: t('user.guest.preferences.datasourceLabels.label')
  };

  const loadDefaultGuestPreferences = async () => {
    // Mount, reconnect and Retry can overlap; only the newest request writes values or the error.
    const request = ++defaultPrefsRequestRef.current;
    defaultPrefsChangedRef.current.clear();
    try {
      if (!defaultPrefsLoadedRef.current) setLoadingDefaultPrefs(true);
      const response = await fetch(
        '/api/system/default-guest-preferences',
        ApiService.getFetchOptions()
      );
      await assertOk(response);
      const preferences = (await response.json()) as DefaultGuestPreferencesResponse;
      if (request !== defaultPrefsRequestRef.current) return;
      const loaded: DefaultGuestPreferences = {
        useLocalTimezone: preferences.useLocalTimezone,
        useUtcTimezone: preferences.useUtcTimezone ?? false,
        use24HourFormat: preferences.use24HourFormat,
        sharpCorners: preferences.sharpCorners,
        disableTooltips: preferences.disableTooltips,
        showDatasourceLabels: preferences.showDatasourceLabels,
        allowedTimeFormats: preferences.allowedTimeFormats ?? [...TIME_SETTING_VALUES]
      };
      setDefaultGuestPreferences((prev: DefaultGuestPreferences) => {
        const next: DefaultGuestPreferences = { ...loaded };
        for (const key of defaultPrefsChangedRef.current) Object.assign(next, { [key]: prev[key] });
        return next;
      });
      defaultPrefsLoadedRef.current = true;
      setDefaultPrefsError(null);
    } catch (err) {
      if (request !== defaultPrefsRequestRef.current) return;
      if (!defaultPrefsLoadedRef.current) {
        setDefaultPrefsError(getErrorMessage(err));
      } else {
        // The card keeps the values it loaded; the next reconnect asks again.
        console.error(
          '[GuestConfiguration] Failed to reload guest defaults:',
          getErrorMessage(err)
        );
      }
    } finally {
      if (request === defaultPrefsRequestRef.current) {
        setLoadingDefaultPrefs(false);
      }
    }
  };

  const handleUpdateDefaultGuestPref = async (key: string, value: boolean) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingDefaultPref(key);
      const pushesBefore = new Map(defaultPrefsPushesRef.current);
      const response = await fetch(
        `/api/system/default-guest-preferences/${key}`,
        ApiService.getJsonFetchOptions({ value }, { method: 'PATCH' })
      );

      await assertOk(response);
      applyDefaultPrefsReply({ [key]: value }, pushesBefore);
    } catch (err: unknown) {
      notifyError(
        t('user.guest.errors.updateDefault', { label: preferenceLabels[key] || key }),
        err,
        { logLabel: 'Failed to update default guest preference' }
      );
    } finally {
      setUpdatingDefaultPref(null);
    }
  };

  const handleDefaultGuestPreferencesChanged = useCallback(
    (data: DefaultGuestPreferencesChangedEvent) => {
      applyDefaultPrefsPush(data.key === 'clock' ? data.clock : { [data.key]: data.value });
    },
    [applyDefaultPrefsPush]
  );

  const handleAllowedTimeFormatsChanged = useCallback(
    (data: { formats: string[] }) => {
      applyDefaultPrefsPush({ allowedTimeFormats: data.formats });
    },
    [applyDefaultPrefsPush]
  );

  // Each service announces its thread limit under its own field name, and the two
  // anonymous services send none at all, so the value is read through the service's
  // capability rather than off a fixed field.
  const handlePrefillConfigChanged = useCallback(
    (service: PrefillServiceConfig, data: GuestPrefillConfigChangedPayload) => {
      setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
        ...prev,
        [service.id]: {
          enabledByDefault: data.enabledByDefault,
          durationHours: data.durationHours,
          maxThreadCount: service.supportsMaxThreads
            ? (data[service.configEventThreadField] ?? null)
            : null
        }
      }));
    },
    []
  );

  const handleAllowedFormatsChange = async (formats: string[]) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingAllowedFormats(true);
      const pushesBefore = new Map(defaultPrefsPushesRef.current);
      const response = await fetch(
        '/api/system/default-guest-preferences/allowed-time-formats',
        ApiService.getJsonFetchOptions({ formats }, { method: 'PATCH' })
      );

      await assertOk(response);
      // If current default is no longer in allowed list, update to first allowed format
      const currentDefault = getCurrentDefaultFormat();
      if (!formats.includes(currentDefault) && formats.length > 0) {
        await updateDefaultTimeFormat(formats[0] as TimeSettingValue);
      }

      applyDefaultPrefsReply({ allowedTimeFormats: formats }, pushesBefore);
    } catch (err: unknown) {
      notifyError(t('user.guest.errors.updateAllowedTimeFormats'), err, {
        logLabel: 'Failed to update allowed time formats'
      });
    } finally {
      setUpdatingAllowedFormats(false);
    }
  };

  // Guest prefill config load/update, driven by PREFILL_SERVICES. Only services that
  // support a thread cap send or read maxThreadCount; the anonymous ones never carry the
  // field on the wire, so it is omitted from their request body and pinned to null locally.
  const loadPrefillConfig = useCallback(async (service: PrefillServiceConfig, request: number) => {
    try {
      setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: true
      }));
      const configResponse = await fetch(service.guestConfigPath, ApiService.getFetchOptions());
      await assertOk(configResponse);
      const config = (await configResponse.json()) as GuestPrefillConfigResponse;
      if (request !== prefillConfigRequestRef.current) return;
      setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
        ...prev,
        [service.id]: toGuestPrefillConfig(service, config)
      }));
    } catch (err) {
      if (request !== prefillConfigRequestRef.current) return;
      // A change held during the read would be sent on top of the panel's starting values.
      setPendingPrefillEdits((prev: Record<GameServiceId, PendingPrefillEdit | null>) => ({
        ...prev,
        [service.id]: null
      }));
      setPrefillConfigError(getErrorMessage(err));
    } finally {
      if (request === prefillConfigRequestRef.current) {
        setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
          ...prev,
          [service.id]: false
        }));
      }
    }
  }, []);

  // Mount, reconnect and Retry each reload every service as one batch, and batches can overlap;
  // only the newest batch writes values, the error or the loading flags.
  const loadPrefillConfigs = useCallback(() => {
    const request = ++prefillConfigRequestRef.current;
    setPrefillConfigError(null);
    for (const service of PREFILL_SERVICES) {
      void loadPrefillConfig(service, request);
    }
  }, [loadPrefillConfig]);

  const updatePrefillConfig = useCallback(
    async (
      service: PrefillServiceConfig,
      field: GuestPrefillConfigField,
      change: Partial<GuestPrefillConfig>
    ) => {
      if (authMode !== 'authenticated') return;
      // One request per service at a time: a change made while its read or save is open is
      // merged into the held change and sent when that request ends.
      if (loadingPrefillConfigs[service.id] || updatingPrefillConfigs[service.id] !== null) {
        setPendingPrefillEdits((prev: Record<GameServiceId, PendingPrefillEdit | null>) => ({
          ...prev,
          [service.id]: { field, edits: { ...prev[service.id]?.edits, ...change } }
        }));
        return;
      }
      const next = { ...prefillConfigs[service.id], ...change };
      try {
        setUpdatingPrefillConfigs(
          (prev: Record<GameServiceId, GuestPrefillConfigField | null>) => ({
            ...prev,
            [service.id]: field
          })
        );
        setSendingPrefillEdits(
          (prev: Record<GameServiceId, Partial<GuestPrefillConfig> | null>) => ({
            ...prev,
            [service.id]: change
          })
        );
        const body: Record<string, unknown> = {
          enabledByDefault: next.enabledByDefault,
          durationHours: next.durationHours
        };
        if (service.supportsMaxThreads) {
          body.maxThreadCount = next.maxThreadCount;
        }
        const response = await fetch(
          service.guestConfigPath,
          ApiService.getJsonFetchOptions(body, { method: 'POST' })
        );

        await assertOk(response);
        const config = (await response.json()) as GuestPrefillConfigResponse;
        setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
          ...prev,
          [service.id]: toGuestPrefillConfig(service, config)
        }));
        showToast('success', t('user.guest.prefill.updated'));
      } catch (err: unknown) {
        // A held change was made on top of the one that failed; the panel falls back to the
        // stored values, so the error is not followed by a save of half the edit.
        setPendingPrefillEdits((prev: Record<GameServiceId, PendingPrefillEdit | null>) => ({
          ...prev,
          [service.id]: null
        }));
        notifyError(t('user.guest.prefill.errors.update'), err, {
          logLabel: `Failed to update ${service.shortName} prefill config`
        });
      } finally {
        setUpdatingPrefillConfigs(
          (prev: Record<GameServiceId, GuestPrefillConfigField | null>) => ({
            ...prev,
            [service.id]: null
          })
        );
        setSendingPrefillEdits(
          (prev: Record<GameServiceId, Partial<GuestPrefillConfig> | null>) => ({
            ...prev,
            [service.id]: null
          })
        );
      }
    },
    [authMode, loadingPrefillConfigs, updatingPrefillConfigs, prefillConfigs, notifyError, t]
  );

  // Sends each held change once its service's read or save has ended.
  useEffect(() => {
    for (const service of PREFILL_SERVICES) {
      const pending = pendingPrefillEdits[service.id];
      if (
        pending === null ||
        loadingPrefillConfigs[service.id] ||
        updatingPrefillConfigs[service.id] !== null
      )
        continue;
      setPendingPrefillEdits((prev: Record<GameServiceId, PendingPrefillEdit | null>) => ({
        ...prev,
        [service.id]: null
      }));
      void updatePrefillConfig(service, pending.field, pending.edits);
    }
  }, [pendingPrefillEdits, loadingPrefillConfigs, updatingPrefillConfigs, updatePrefillConfig]);

  // What the panel shows: the stored values with the change being sent and the held one on top.
  const shownPrefillConfig = (service: PrefillServiceConfig): GuestPrefillConfig => ({
    ...prefillConfigs[service.id],
    ...sendingPrefillEdits[service.id],
    ...pendingPrefillEdits[service.id]?.edits
  });

  // Handler callbacks for PrefillServicePanel
  const handleToggleEnabled = (service: PrefillServiceConfig) => {
    void updatePrefillConfig(service, 'enabled', {
      enabledByDefault: !shownPrefillConfig(service).enabledByDefault
    });
  };

  const handleDurationChange = (service: PrefillServiceConfig, hours: number) => {
    void updatePrefillConfig(service, 'duration', { durationHours: hours });
  };

  // Services without a thread cap hide the control entirely, so this can only fire for
  // services that support one; the guard keeps a stray call from posting the field.
  const handleMaxThreadsChange = (service: PrefillServiceConfig, threads: number | null) => {
    if (!service.supportsMaxThreads) return;
    void updatePrefillConfig(service, 'maxThreads', { maxThreadCount: threads });
  };

  // Refresh guest defaults when SignalR reconnects (catches config events missed during disconnect)
  useReconnectRefetch(isConnected, () => {
    loadDefaultGuestPreferences();
    loadPrefillConfigs();
  });

  useEffect(() => {
    loadDefaultGuestPreferences();

    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);

    // Bind one subscription per service. The handlers are built here rather than in the
    // table so each one closes over its own service and can be passed to off() unchanged.
    const serviceSubscriptions = PREFILL_SERVICES.map((service: PrefillServiceConfig) => ({
      eventName: service.guestConfigChangedEvent,
      handler: (data: GuestPrefillConfigChangedPayload) => handlePrefillConfigChanged(service, data)
    }));

    loadPrefillConfigs();
    for (const subscription of serviceSubscriptions) {
      on(subscription.eventName, subscription.handler);
    }

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
      for (const subscription of serviceSubscriptions) {
        off(subscription.eventName, subscription.handler);
      }
    };
  }, [
    on,
    off,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged,
    handlePrefillConfigChanged,
    loadPrefillConfigs
  ]);

  const helpAccessory = (
    <HelpPopover position="left" width={320}>
      <HelpSection title={t('user.guest.prefill.help.aboutTitle')}>
        {t('user.guest.prefill.sectionSubtitle')}
      </HelpSection>
    </HelpPopover>
  );

  const serviceDescriptions: Record<GameServiceId, string> = {
    steam: t('user.guest.prefill.services.steamDescription'),
    epic: t('user.guest.prefill.services.epicDescription'),
    battlenet: t('user.guest.prefill.services.battlenetDescription'),
    riot: t('user.guest.prefill.services.riotDescription'),
    xbox: t('user.guest.prefill.services.xboxDescription')
  };

  return (
    <div>
      <GroupHeading label={t('user.groups.guestDefaults')} actions={<AccordionGroupToggle />} />

      <div className="space-y-4">
        <AccessSecurityCard
          guestDurationHours={guestDurationHours}
          onDurationChange={onDurationChange}
          updatingDuration={updatingDuration}
          durationOptions={translatedDurationOptions}
        />

        <AccordionSection
          title={t('user.guest.prefill.sectionTitle')}
          titleAccessory={helpAccessory}
          icon={Download}
          isExpanded={prefillSectionExpanded}
          onToggle={() => setPrefillSectionExpanded((prev) => !prev)}
          count={enabledPrefillCount}
          badge={
            <SectionHeaderActions>
              {prefillConfigError !== null && !prefillSectionExpanded && <SectionErrorChip />}
              <SectionActionsMenu label={t('management.actions.menuLabel')}>
                {(close) => (
                  <ActionMenuItem
                    icon={
                      allPrefillServicesExpanded ? (
                        <ChevronsDownUp className="w-3.5 h-3.5" />
                      ) : (
                        <ChevronsUpDown className="w-3.5 h-3.5" />
                      )
                    }
                    disabled={!prefillSectionExpanded}
                    onClick={() => {
                      handlePrefillExpandCollapseAll();
                      close();
                    }}
                  >
                    {allPrefillServicesExpanded
                      ? t('management.gameDetection.collapseAll')
                      : t('management.gameDetection.expandAll')}
                  </ActionMenuItem>
                )}
              </SectionActionsMenu>
            </SectionHeaderActions>
          }
        >
          <div className="space-y-4">
            {prefillConfigError !== null && (
              <ErrorBlock
                title={t('user.guest.prefill.errors.loadConfig')}
                message={prefillConfigError}
                retryLabel={t('common.retry')}
                onRetry={loadPrefillConfigs}
              />
            )}
            <p className="text-xs text-themed-muted">
              {t('user.guest.prefill.existingGuestsNote')}
            </p>
            {/* The panels start at default values, so a failed read shows only the box above */}
            {prefillConfigError === null && (
              <div className="user-settings-service-sections">
                {PREFILL_SERVICES.map((service: PrefillServiceConfig) => (
                  <PrefillServicePanel
                    key={service.id}
                    serviceName={service.displayName}
                    serviceIcon={service.icon}
                    iconColor={service.colorVar}
                    config={shownPrefillConfig(service)}
                    onToggleEnabled={() => handleToggleEnabled(service)}
                    onDurationChange={(hours: number) => handleDurationChange(service, hours)}
                    onMaxThreadsChange={(threads: number | null) =>
                      handleMaxThreadsChange(service, threads)
                    }
                    updating={updatingPrefillConfigs[service.id]}
                    warningText={t('user.guest.prefill.warning')}
                    durationLabel={t('user.guest.prefill.duration.label')}
                    durationHelpText={t('user.guest.prefill.duration.description')}
                    maxThreadsLabel={
                      service.supportsMaxThreads
                        ? t('user.guest.prefill.maxThreads.label')
                        : undefined
                    }
                    enableLabel={t('user.guest.prefill.enableByDefault.label')}
                    enableDescription={t('user.guest.prefill.enableByDefault.description')}
                    serviceDescription={serviceDescriptions[service.id]}
                    prefillDurationOptions={prefillDurationOptions}
                    maxThreadOptions={service.supportsMaxThreads ? maxThreadOptions : undefined}
                    showMaxThreads={service.supportsMaxThreads}
                    isExpanded={prefillServiceExpanded[service.id]}
                    onToggle={() => togglePrefillService(service.id)}
                  />
                ))}
              </div>
            )}
          </div>
        </AccordionSection>

        {/* The card starts at default values, so a failed read shows the box in its place */}
        {defaultPrefsError !== null ? (
          <ErrorBlock
            title={t('user.guest.errors.loadPreferences')}
            message={defaultPrefsError}
            retryLabel={t('common.retry')}
            onRetry={() => void loadDefaultGuestPreferences()}
          />
        ) : (
          <AppearanceDisplayCard
            defaultGuestTheme={defaultGuestTheme}
            onGuestThemeChange={onGuestThemeChange}
            updatingGuestTheme={updatingGuestTheme}
            availableThemes={availableThemes}
            defaultGuestRefreshRate={defaultGuestRefreshRate}
            onGuestRefreshRateChange={onGuestRefreshRateChange}
            updatingGuestRefreshRate={updatingGuestRefreshRate}
            guestRefreshRateLocked={guestRefreshRateLocked}
            onGuestRefreshRateLockChange={onGuestRefreshRateLockChange}
            updatingGuestRefreshRateLock={updatingGuestRefreshRateLock}
            refreshRateOptions={translatedRefreshRateOptions}
            defaultGuestPreferences={defaultGuestPreferences}
            onUpdateDefaultPref={handleUpdateDefaultGuestPref}
            updatingDefaultPref={updatingDefaultPref}
            loadingDefaultPrefs={loadingDefaultPrefs}
            onAllowedFormatsChange={handleAllowedFormatsChange}
            updatingAllowedFormats={updatingAllowedFormats}
          />
        )}
      </div>
    </div>
  );
};

export default GuestConfiguration;
