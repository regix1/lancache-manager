import React, { useEffect, useState, useCallback, useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { Download, ChevronsDownUp, ChevronsUpDown } from 'lucide-react';
import { AccordionSection } from '@components/ui/AccordionSection';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import { useAccordionGroupItem } from '@contexts/AccordionGroupContext';
import { SectionActionsMenu } from '@components/ui/SectionActionsMenu';
import { SectionHeaderActions } from '@components/ui/SectionHeaderActions';
import { ActionMenuItem } from '@components/ui/ActionMenu';
import ApiService from '@services/api.service';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { useAuth } from '@contexts/useAuth';
import {
  PREFILL_SERVICES,
  prefillServiceRecord,
  type GuestPrefillConfigChangedPayload,
  type PrefillServiceConfig
} from '@components/features/prefill/hooks/prefillServiceConfig';
import type { GameServiceId } from '@/types/gameService';
import { type ThemeOption, durationOptions, refreshRateOptions, showToast } from './types';
import AccessSecurityCard from './AccessSecurityCard';
import PrefillServicePanel from './PrefillServicePanel';
import AppearanceDisplayCard from './AppearanceDisplayCard';
import '@components/features/management/managementSectionContent.css';
import './user-settings.css';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

/** Guest default prefill permissions for one service. */
interface GuestPrefillConfig {
  enabledByDefault: boolean;
  durationHours: number;
  maxThreadCount: number | null;
}

interface DefaultGuestPreferences {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  allowedTimeFormats: string[];
}

interface DefaultGuestPreferencesResponse {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  allowedTimeFormats?: string[];
}

interface GuestPrefillConfigResponse {
  enabledByDefault: boolean;
  durationHours: number;
  maxThreadCount?: number | null;
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
  const { on, off } = useSignalR();
  const { authMode } = useAuth();
  const [defaultGuestPreferences, setDefaultGuestPreferences] = useState<DefaultGuestPreferences>({
    useLocalTimezone: false,
    use24HourFormat: true,
    sharpCorners: false,
    disableTooltips: false,
    showDatasourceLabels: true,
    showYearInDates: false,
    allowedTimeFormats: ['server-24h', 'server-12h', 'local-24h', 'local-12h']
  });
  const [loadingDefaultPrefs, setLoadingDefaultPrefs] = useState(false);
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
    Record<GameServiceId, boolean>
  >(() => prefillServiceRecord<boolean>(() => false));

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

  // Helper to update default time format based on a format value
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const newUseLocal = format.startsWith('local');
    const newUse24Hour = format.endsWith('24h');

    const [localResponse, formatResponse] = await Promise.all([
      fetch(
        '/api/system/default-guest-preferences/useLocalTimezone',
        ApiService.getJsonFetchOptions({ value: newUseLocal }, { method: 'PATCH' })
      ),
      fetch(
        '/api/system/default-guest-preferences/use24HourFormat',
        ApiService.getJsonFetchOptions({ value: newUse24Hour }, { method: 'PATCH' })
      )
    ]);

    if (localResponse.ok && formatResponse.ok) {
      setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
        ...prev,
        useLocalTimezone: newUseLocal,
        use24HourFormat: newUse24Hour
      }));
    }
  };

  // Get current default time format from the two boolean settings
  const getCurrentDefaultFormat = (): TimeSettingValue => {
    const isLocal = defaultGuestPreferences.useLocalTimezone;
    const is24h = defaultGuestPreferences.use24HourFormat;
    if (isLocal && is24h) return 'local-24h';
    if (isLocal && !is24h) return 'local-12h';
    if (!isLocal && is24h) return 'server-24h';
    return 'server-12h';
  };

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
  const THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256];
  const maxThreadOptions = [
    { value: '', label: t('user.guest.prefill.maxThreads.noLimit') },
    ...THREAD_VALUES.map((n: number) => ({
      value: String(n),
      label: t('user.guest.prefill.maxThreads.threadsCount', { count: n })
    }))
  ];
  const preferenceLabels: Record<string, string> = {
    showYearInDates: t('user.guest.preferences.showYear.label'),
    sharpCorners: t('user.guest.preferences.sharpCorners.label'),
    disableTooltips: t('user.guest.preferences.disableTooltips.label'),
    showDatasourceLabels: t('user.guest.preferences.datasourceLabels.label')
  };

  const loadDefaultGuestPreferences = async () => {
    try {
      setLoadingDefaultPrefs(true);
      const response = await fetch(
        '/api/system/default-guest-preferences',
        ApiService.getFetchOptions()
      );
      if (response.ok) {
        const data = (await response.json()) as DefaultGuestPreferencesResponse;
        setDefaultGuestPreferences({
          useLocalTimezone: data.useLocalTimezone,
          use24HourFormat: data.use24HourFormat,
          sharpCorners: data.sharpCorners,
          disableTooltips: data.disableTooltips,
          showDatasourceLabels: data.showDatasourceLabels,
          showYearInDates: data.showYearInDates,
          allowedTimeFormats: data.allowedTimeFormats ?? [
            'server-24h',
            'server-12h',
            'local-24h',
            'local-12h'
          ]
        });
      }
    } catch (err) {
      notifyError(t('user.guest.errors.loadPreferences'), err, {
        logLabel: 'Failed to load default guest preferences'
      });
    } finally {
      setLoadingDefaultPrefs(false);
    }
  };

  const handleUpdateDefaultGuestPref = async (key: string, value: boolean) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingDefaultPref(key);
      const response = await fetch(
        `/api/system/default-guest-preferences/${key}`,
        ApiService.getJsonFetchOptions({ value }, { method: 'PATCH' })
      );

      if (response.ok) {
        setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
          ...prev,
          [key]: value
        }));
      } else {
        const errorData = await response.json();
        notifyError(
          t('user.guest.errors.updateDefault', { label: preferenceLabels[key] || key }),
          errorData?.error ? new Error(errorData.error) : undefined,
          { logLabel: 'Failed to update default guest preference' }
        );
      }
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
    (data: { key: string; value: boolean }) => {
      setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
        ...prev,
        [data.key]: data.value
      }));
    },
    []
  );

  const handleAllowedTimeFormatsChanged = useCallback((data: { formats: string[] }) => {
    setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
      ...prev,
      allowedTimeFormats: data.formats
    }));
  }, []);

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
      const response = await fetch(
        '/api/system/default-guest-preferences/allowed-time-formats',
        ApiService.getJsonFetchOptions({ formats }, { method: 'PATCH' })
      );

      if (response.ok) {
        // If current default is no longer in allowed list, update to first allowed format
        const currentDefault = getCurrentDefaultFormat();
        if (!formats.includes(currentDefault) && formats.length > 0) {
          await updateDefaultTimeFormat(formats[0] as TimeSettingValue);
        }

        setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
          ...prev,
          allowedTimeFormats: formats
        }));
      } else {
        const errorData = await response.json();
        notifyError(
          t('user.guest.errors.updateAllowedTimeFormats'),
          errorData?.error ? new Error(errorData.error) : undefined,
          { logLabel: 'Failed to update allowed time formats' }
        );
      }
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
  const loadPrefillConfig = async (service: PrefillServiceConfig) => {
    try {
      setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: true
      }));
      const configResponse = await fetch(service.guestConfigPath, ApiService.getFetchOptions());
      if (configResponse.ok) {
        const data = (await configResponse.json()) as GuestPrefillConfigResponse;
        setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
          ...prev,
          [service.id]: {
            enabledByDefault: data.enabledByDefault,
            durationHours: data.durationHours,
            maxThreadCount: service.supportsMaxThreads ? (data.maxThreadCount ?? null) : null
          }
        }));
      }
    } catch (err) {
      notifyError(t('user.guest.prefill.errors.loadConfig'), err, {
        logLabel: `Failed to load ${service.shortName} prefill config`
      });
    } finally {
      setLoadingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: false
      }));
    }
  };

  const updatePrefillConfig = async (
    service: PrefillServiceConfig,
    enabledByDefault: boolean,
    durationHours: number,
    maxThreadCount?: number | null
  ) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: true
      }));
      const body: Record<string, unknown> = { enabledByDefault, durationHours };
      if (service.supportsMaxThreads) {
        body.maxThreadCount =
          maxThreadCount !== undefined ? maxThreadCount : prefillConfigs[service.id].maxThreadCount;
      }
      const response = await fetch(
        service.guestConfigPath,
        ApiService.getJsonFetchOptions(body, { method: 'POST' })
      );

      if (response.ok) {
        const data = (await response.json()) as GuestPrefillConfigResponse;
        setPrefillConfigs((prev: Record<GameServiceId, GuestPrefillConfig>) => ({
          ...prev,
          [service.id]: {
            enabledByDefault: data.enabledByDefault,
            durationHours: data.durationHours,
            maxThreadCount: service.supportsMaxThreads ? (data.maxThreadCount ?? null) : null
          }
        }));
        showToast('success', t('user.guest.prefill.updated'));
      } else {
        const errorData = await response.json();
        notifyError(
          t('user.guest.prefill.errors.update'),
          errorData?.error ? new Error(errorData.error) : undefined,
          { logLabel: `Failed to update ${service.shortName} prefill config` }
        );
      }
    } catch (err: unknown) {
      notifyError(t('user.guest.prefill.errors.update'), err, {
        logLabel: `Failed to update ${service.shortName} prefill config`
      });
    } finally {
      setUpdatingPrefillConfigs((prev: Record<GameServiceId, boolean>) => ({
        ...prev,
        [service.id]: false
      }));
    }
  };

  // Handler callbacks for PrefillServicePanel
  const handleToggleEnabled = (service: PrefillServiceConfig) => {
    const config = prefillConfigs[service.id];
    updatePrefillConfig(service, !config.enabledByDefault, config.durationHours);
  };

  const handleDurationChange = (service: PrefillServiceConfig, hours: number) => {
    updatePrefillConfig(service, prefillConfigs[service.id].enabledByDefault, hours);
  };

  // Services without a thread cap hide the control entirely, so this can only fire for
  // services that support one; the guard keeps a stray call from posting the field.
  const handleMaxThreadsChange = (service: PrefillServiceConfig, threads: number | null) => {
    if (!service.supportsMaxThreads) return;
    const config = prefillConfigs[service.id];
    updatePrefillConfig(service, config.enabledByDefault, config.durationHours, threads);
  };

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

    for (const service of PREFILL_SERVICES) {
      loadPrefillConfig(service);
    }
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    on,
    off,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged,
    handlePrefillConfigChanged
  ]);

  return (
    <div>
      <div className="flex items-center justify-between gap-2 mb-3 sm:mb-4">
        <div className="flex items-center gap-2 min-w-0">
          <div className="w-1 h-5 rounded-full bg-[var(--theme-icon-purple)]" />
          <h3 className="text-sm font-semibold text-themed-secondary uppercase tracking-wide">
            {t('user.groups.guestDefaults')}
          </h3>
        </div>
        <AccordionGroupToggle />
      </div>

      <div className="space-y-4">
        <AccessSecurityCard
          guestDurationHours={guestDurationHours}
          onDurationChange={onDurationChange}
          updatingDuration={updatingDuration}
          durationOptions={translatedDurationOptions}
        />

        <AccordionSection
          title={t('user.guest.prefill.sectionTitle')}
          description={t('user.guest.prefill.sectionSubtitle')}
          icon={Download}
          iconColor="var(--theme-icon-blue)"
          isExpanded={prefillSectionExpanded}
          onToggle={() => setPrefillSectionExpanded((prev) => !prev)}
          count={enabledPrefillCount}
          badge={
            <SectionHeaderActions>
              <SectionActionsMenu label={t('management.actions.menuLabel', 'Actions')}>
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
            <p className="text-xs text-themed-muted">
              {t('user.guest.prefill.existingGuestsNote')}
            </p>
            <div className="user-settings-service-sections">
              {PREFILL_SERVICES.map((service: PrefillServiceConfig) => (
                <PrefillServicePanel
                  key={service.id}
                  serviceName={service.displayName}
                  serviceIcon={service.icon}
                  iconColor={service.colorVar}
                  config={prefillConfigs[service.id]}
                  onToggleEnabled={() => handleToggleEnabled(service)}
                  onDurationChange={(hours: number) => handleDurationChange(service, hours)}
                  onMaxThreadsChange={(threads: number | null) =>
                    handleMaxThreadsChange(service, threads)
                  }
                  loading={loadingPrefillConfigs[service.id]}
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
                  prefillDurationOptions={prefillDurationOptions}
                  maxThreadOptions={service.supportsMaxThreads ? maxThreadOptions : undefined}
                  showMaxThreads={service.supportsMaxThreads}
                  isExpanded={prefillServiceExpanded[service.id]}
                  onToggle={() => togglePrefillService(service.id)}
                />
              ))}
            </div>
          </div>
        </AccordionSection>

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
      </div>
    </div>
  );
};

export default GuestConfiguration;
