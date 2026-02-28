import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Download } from 'lucide-react';
import { Card } from '@components/ui/Card';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import { SteamIcon } from '@components/ui/SteamIcon';
import { EpicIcon } from '@components/ui/EpicIcon';
import { type ThemeOption, durationOptions, refreshRateOptions, showToast } from './types';
import AccessSecurityCard from './AccessSecurityCard';
import PrefillServicePanel from './PrefillServicePanel';
import AppearanceDisplayCard from './AppearanceDisplayCard';

type TimeSettingValue = 'server-24h' | 'server-12h' | 'local-24h' | 'local-12h';

interface DefaultGuestPreferences {
  useLocalTimezone: boolean;
  use24HourFormat: boolean;
  sharpCorners: boolean;
  disableTooltips: boolean;
  showDatasourceLabels: boolean;
  showYearInDates: boolean;
  allowedTimeFormats: string[];
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

  // Steam Prefill permission state
  const [prefillConfig, setPrefillConfig] = useState({
    enabledByDefault: false,
    durationHours: 2,
    maxThreadCount: null as number | null
  });
  const [loadingPrefillConfig, setLoadingPrefillConfig] = useState(false);
  const [updatingPrefillConfig, setUpdatingPrefillConfig] = useState(false);

  // Epic Prefill permission state
  const [epicPrefillConfig, setEpicPrefillConfig] = useState({
    enabledByDefault: false,
    durationHours: 2,
    maxThreadCount: null as number | null
  });
  const [loadingEpicPrefillConfig, setLoadingEpicPrefillConfig] = useState(false);
  const [updatingEpicPrefillConfig, setUpdatingEpicPrefillConfig] = useState(false);

  // Helper to update default time format based on a format value
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const newUseLocal = format.startsWith('local');
    const newUse24Hour = format.endsWith('24h');

    const [localResponse, formatResponse] = await Promise.all([
      fetch(
        '/api/system/default-guest-preferences/useLocalTimezone',
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ value: newUseLocal })
        })
      ),
      fetch(
        '/api/system/default-guest-preferences/use24HourFormat',
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ value: newUse24Hour })
        })
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
    { value: '2', label: t('user.guest.prefillDurationOptions.2') }
  ];
  const THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256];
  const maxThreadOptions = [
    { value: '', label: t('user.guest.prefill.maxThreads.noLimit') },
    ...THREAD_VALUES.map((n: number) => ({
      value: String(n),
      label: `${n} threads`
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
        const data = await response.json();
        setDefaultGuestPreferences({
          useLocalTimezone: data.useLocalTimezone ?? false,
          use24HourFormat: data.use24HourFormat ?? true,
          sharpCorners: data.sharpCorners ?? false,
          disableTooltips: data.disableTooltips ?? false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          showYearInDates: data.showYearInDates ?? false,
          allowedTimeFormats: data.allowedTimeFormats ?? [
            'server-24h',
            'server-12h',
            'local-24h',
            'local-12h'
          ]
        });
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.guest.errors.loadPreferences'));
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
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ value })
        })
      );

      if (response.ok) {
        setDefaultGuestPreferences((prev: DefaultGuestPreferences) => ({
          ...prev,
          [key]: value
        }));
      } else {
        const errorData = await response.json();
        showToast(
          'error',
          errorData.error ||
            t('user.guest.errors.updateDefault', {
              label: preferenceLabels[key] || key
            })
        );
      }
    } catch (err: unknown) {
      showToast(
        'error',
        getErrorMessage(err) ||
          t('user.guest.errors.updateDefault', {
            label: preferenceLabels[key] || key
          })
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

  const handlePrefillConfigChanged = useCallback(
    (data: {
      enabledByDefault: boolean;
      durationHours: number;
      maxThreadCount?: number | null;
    }) => {
      setPrefillConfig({
        enabledByDefault: data.enabledByDefault,
        durationHours: data.durationHours,
        maxThreadCount: data.maxThreadCount ?? null
      });
    },
    []
  );

  const handleEpicPrefillConfigChanged = useCallback(
    (data: {
      enabledByDefault: boolean;
      durationHours: number;
      maxThreadCount?: number | null;
    }) => {
      setEpicPrefillConfig({
        enabledByDefault: data.enabledByDefault,
        durationHours: data.durationHours,
        maxThreadCount: data.maxThreadCount ?? null
      });
    },
    []
  );

  const handleAllowedFormatsChange = async (formats: string[]) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingAllowedFormats(true);
      const response = await fetch(
        '/api/system/default-guest-preferences/allowed-time-formats',
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ formats })
        })
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
        showToast('error', errorData.error || t('user.guest.errors.updateAllowedTimeFormats'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.guest.errors.updateAllowedTimeFormats'));
    } finally {
      setUpdatingAllowedFormats(false);
    }
  };

  // Prefill config functions
  const loadPrefillConfig = async () => {
    try {
      setLoadingPrefillConfig(true);
      const configResponse = await fetch(
        '/api/auth/guest/prefill/config',
        ApiService.getFetchOptions()
      );
      if (configResponse.ok) {
        const data = await configResponse.json();
        setPrefillConfig({
          enabledByDefault: data.enabledByDefault ?? false,
          durationHours: data.durationHours ?? 2,
          maxThreadCount: data.maxThreadCount ?? null
        });
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.guest.prefill.errors.loadConfig'));
    } finally {
      setLoadingPrefillConfig(false);
    }
  };

  const updatePrefillConfig = async (
    enabledByDefault: boolean,
    durationHours: number,
    maxThreadCount?: number | null
  ) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingPrefillConfig(true);
      const body: Record<string, unknown> = { enabledByDefault, durationHours };
      if (maxThreadCount !== undefined) {
        body.maxThreadCount = maxThreadCount;
      } else {
        body.maxThreadCount = prefillConfig.maxThreadCount;
      }
      const response = await fetch(
        '/api/auth/guest/prefill/config',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify(body)
        })
      );

      if (response.ok) {
        const data = await response.json();
        setPrefillConfig({
          enabledByDefault: data.enabledByDefault,
          durationHours: data.durationHours,
          maxThreadCount: data.maxThreadCount ?? null
        });
        showToast('success', t('user.guest.prefill.updated'));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.guest.prefill.errors.update'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.guest.prefill.errors.update'));
    } finally {
      setUpdatingPrefillConfig(false);
    }
  };

  // Epic Prefill config functions
  const loadEpicPrefillConfig = async () => {
    try {
      setLoadingEpicPrefillConfig(true);
      const configResponse = await fetch(
        '/api/auth/guest/epic-prefill/config',
        ApiService.getFetchOptions()
      );
      if (configResponse.ok) {
        const data = await configResponse.json();
        setEpicPrefillConfig({
          enabledByDefault: data.enabledByDefault ?? false,
          durationHours: data.durationHours ?? 2,
          maxThreadCount: data.maxThreadCount ?? null
        });
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.guest.prefill.errors.loadConfig'));
    } finally {
      setLoadingEpicPrefillConfig(false);
    }
  };

  const updateEpicPrefillConfig = async (
    enabledByDefault: boolean,
    durationHours: number,
    maxThreadCount?: number | null
  ) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingEpicPrefillConfig(true);
      const body: Record<string, unknown> = { enabledByDefault, durationHours };
      if (maxThreadCount !== undefined) {
        body.maxThreadCount = maxThreadCount;
      } else {
        body.maxThreadCount = epicPrefillConfig.maxThreadCount;
      }
      const response = await fetch(
        '/api/auth/guest/epic-prefill/config',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify(body)
        })
      );

      if (response.ok) {
        const data = await response.json();
        setEpicPrefillConfig({
          enabledByDefault: data.enabledByDefault,
          durationHours: data.durationHours,
          maxThreadCount: data.maxThreadCount ?? null
        });
        showToast('success', t('user.guest.prefill.updated'));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.guest.prefill.errors.update'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.guest.prefill.errors.update'));
    } finally {
      setUpdatingEpicPrefillConfig(false);
    }
  };

  // Handler callbacks for PrefillServicePanel
  const handleSteamToggleEnabled = () => {
    updatePrefillConfig(!prefillConfig.enabledByDefault, prefillConfig.durationHours);
  };

  const handleSteamDurationChange = (hours: number) => {
    updatePrefillConfig(prefillConfig.enabledByDefault, hours);
  };

  const handleSteamMaxThreadsChange = (threads: number | null) => {
    updatePrefillConfig(prefillConfig.enabledByDefault, prefillConfig.durationHours, threads);
  };

  const handleEpicToggleEnabled = () => {
    updateEpicPrefillConfig(!epicPrefillConfig.enabledByDefault, epicPrefillConfig.durationHours);
  };

  const handleEpicDurationChange = (hours: number) => {
    updateEpicPrefillConfig(epicPrefillConfig.enabledByDefault, hours);
  };

  const handleEpicMaxThreadsChange = (threads: number | null) => {
    updateEpicPrefillConfig(
      epicPrefillConfig.enabledByDefault,
      epicPrefillConfig.durationHours,
      threads
    );
  };

  useEffect(() => {
    loadDefaultGuestPreferences();
    loadPrefillConfig();
    loadEpicPrefillConfig();

    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    on('GuestPrefillConfigChanged', handlePrefillConfigChanged);
    on('EpicGuestPrefillConfigChanged', handleEpicPrefillConfigChanged);

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
      off('GuestPrefillConfigChanged', handlePrefillConfigChanged);
      off('EpicGuestPrefillConfigChanged', handleEpicPrefillConfigChanged);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    on,
    off,
    handleDefaultGuestPreferencesChanged,
    handleAllowedTimeFormatsChanged,
    handlePrefillConfigChanged,
    handleEpicPrefillConfigChanged
  ]);

  return (
    <div className="space-y-4">
      {/* Access & Security card */}
      <AccessSecurityCard
        guestDurationHours={guestDurationHours}
        onDurationChange={onDurationChange}
        updatingDuration={updatingDuration}
        durationOptions={translatedDurationOptions}
      />

      {/* Prefill Services card */}
      <Card padding="none">
        <div className="p-4 sm:p-5 border-b border-themed-secondary">
          <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
            <Download className="w-5 h-5 text-themed-accent" />
            Prefill Services
          </h3>
          <p className="text-sm mt-1 text-themed-muted">
            Control guest prefill permissions per service
          </p>
        </div>
        <div className="p-4 sm:p-5 space-y-4">
          <PrefillServicePanel
            serviceName="Steam"
            serviceNameClass="text-steam"
            serviceIcon={<SteamIcon size={14} />}
            accentClass="settings-group--steam"
            config={prefillConfig}
            onToggleEnabled={handleSteamToggleEnabled}
            onDurationChange={handleSteamDurationChange}
            onMaxThreadsChange={handleSteamMaxThreadsChange}
            loading={loadingPrefillConfig}
            updating={updatingPrefillConfig}
            warningText={t('user.guest.prefill.warning')}
            durationLabel={t('user.guest.prefill.duration.label')}
            maxThreadsLabel={t('user.guest.prefill.maxThreads.label')}
            enableLabel={t('user.guest.prefill.enableByDefault.label')}
            enableDescription={t('user.guest.prefill.enableByDefault.description')}
            prefillDurationOptions={prefillDurationOptions}
            maxThreadOptions={maxThreadOptions}
          />
          <PrefillServicePanel
            serviceName="Epic Games"
            serviceNameClass="text-epic"
            serviceIcon={<EpicIcon size={14} />}
            accentClass="settings-group--epic"
            config={epicPrefillConfig}
            onToggleEnabled={handleEpicToggleEnabled}
            onDurationChange={handleEpicDurationChange}
            onMaxThreadsChange={handleEpicMaxThreadsChange}
            loading={loadingEpicPrefillConfig}
            updating={updatingEpicPrefillConfig}
            warningText={t('user.guest.prefill.warning')}
            durationLabel={t('user.guest.prefill.duration.label')}
            maxThreadsLabel={t('user.guest.prefill.maxThreads.label')}
            enableLabel={t('user.guest.prefill.enableByDefault.label')}
            enableDescription={t('user.guest.prefill.enableByDefault.description')}
            prefillDurationOptions={prefillDurationOptions}
            maxThreadOptions={maxThreadOptions}
          />
        </div>
      </Card>

      {/* Appearance & Display card - spans full width as last child */}
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
  );
};

export default GuestConfiguration;
