import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Settings2, Loader2, Globe, MapPin, Download, AlertTriangle, Lock, Unlock, Network } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext';
import { useAuth } from '@contexts/AuthContext';
import { ThemeOption, durationOptions, refreshRateOptions, showToast } from './types';

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

  // Prefill permission state
  const [prefillConfig, setPrefillConfig] = useState({
    enabledByDefault: false,
    durationHours: 2,
    maxThreadCount: null as number | null
  });
  const [loadingPrefillConfig, setLoadingPrefillConfig] = useState(false);
  const [updatingPrefillConfig, setUpdatingPrefillConfig] = useState(false);


  // Helper to update default time format based on a format value
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const newUseLocal = format.startsWith('local');
    const newUse24Hour = format.endsWith('24h');

    const [localResponse, formatResponse] = await Promise.all([
      fetch('/api/system/default-guest-preferences/useLocalTimezone', ApiService.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: newUseLocal })
      })),
      fetch('/api/system/default-guest-preferences/use24HourFormat', ApiService.getFetchOptions({
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: newUse24Hour })
      }))
    ]);

    if (localResponse.ok && formatResponse.ok) {
      setDefaultGuestPreferences(prev => ({
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

  const timeFormatOptions = [
    {
      value: 'server-24h',
      label: t('user.guest.timeFormats.server24h.label'),
      description: t('user.guest.timeFormats.server24h.description'),
      icon: Globe
    },
    {
      value: 'server-12h',
      label: t('user.guest.timeFormats.server12h.label'),
      description: t('user.guest.timeFormats.server12h.description'),
      icon: Globe
    },
    {
      value: 'local-24h',
      label: t('user.guest.timeFormats.local24h.label'),
      description: t('user.guest.timeFormats.local24h.description'),
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: t('user.guest.timeFormats.local12h.label'),
      description: t('user.guest.timeFormats.local12h.description'),
      icon: MapPin
    }
  ];
  const translatedDurationOptions = durationOptions.map((option) => ({
    ...option,
    label: t(`user.guest.durationOptions.${option.value}`)
  }));
  const translatedRefreshRateOptions = refreshRateOptions.map((option) => ({
    ...option,
    label: t(`user.guest.refreshRates.${option.value}`)
  }));
  const prefillDurationOptions = [
    { value: '1', label: t('user.guest.prefillDurationOptions.1') },
    { value: '2', label: t('user.guest.prefillDurationOptions.2') }
  ];
  const THREAD_VALUES = [1, 2, 4, 8, 16, 32, 64, 128, 256];
  const maxThreadOptions = [
    { value: '', label: t('user.guest.prefill.maxThreads.noLimit') },
    ...THREAD_VALUES.map((n) => ({
      value: String(n),
      label: `${n} threads`,
      disabled: false
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
      const response = await fetch('/api/system/default-guest-preferences', ApiService.getFetchOptions());
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestPreferences({
          useLocalTimezone: data.useLocalTimezone ?? false,
          use24HourFormat: data.use24HourFormat ?? true,
          sharpCorners: data.sharpCorners ?? false,
          disableTooltips: data.disableTooltips ?? false,
          showDatasourceLabels: data.showDatasourceLabels ?? true,
          showYearInDates: data.showYearInDates ?? false,
          allowedTimeFormats: data.allowedTimeFormats ?? ['server-24h', 'server-12h', 'local-24h', 'local-12h']
        });
      }
    } catch (err) {
      console.error('Failed to load default guest preferences:', err);
    } finally {
      setLoadingDefaultPrefs(false);
    }
  };

  const handleUpdateDefaultGuestPref = async (key: string, value: boolean) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingDefaultPref(key);
      const response = await fetch(`/api/system/default-guest-preferences/${key}`, ApiService.getFetchOptions({
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ value })
      }));

      if (response.ok) {
        setDefaultGuestPreferences((prev) => ({
          ...prev,
          [key]: value
        }));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.guest.errors.updateDefault', {
          label: preferenceLabels[key] || key
        }));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.guest.errors.updateDefault', {
        label: preferenceLabels[key] || key
      }));
    } finally {
      setUpdatingDefaultPref(null);
    }
  };

  const handleDefaultGuestPreferencesChanged = useCallback(
    (data: { key: string; value: boolean }) => {
      setDefaultGuestPreferences((prev) => ({
        ...prev,
        [data.key]: data.value
      }));
    },
    []
  );

  const handleAllowedTimeFormatsChanged = useCallback(
    (data: { formats: string[] }) => {
      setDefaultGuestPreferences((prev) => ({
        ...prev,
        allowedTimeFormats: data.formats
      }));
    },
    []
  );

  const handlePrefillConfigChanged = useCallback(
    (data: { enabledByDefault: boolean; durationHours: number; maxThreadCount?: number | null }) => {
      setPrefillConfig({
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
      const response = await fetch('/api/system/default-guest-preferences/allowed-time-formats', ApiService.getFetchOptions({
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ formats })
      }));

      if (response.ok) {
        // If current default is no longer in allowed list, update to first allowed format
        const currentDefault = getCurrentDefaultFormat();
        if (!formats.includes(currentDefault) && formats.length > 0) {
          await updateDefaultTimeFormat(formats[0] as TimeSettingValue);
        }

        setDefaultGuestPreferences((prev) => ({
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
      const configResponse = await fetch('/api/auth/guest/prefill/config', ApiService.getFetchOptions());
      if (configResponse.ok) {
        const data = await configResponse.json();
        setPrefillConfig({
          enabledByDefault: data.enabledByDefault ?? false,
          durationHours: data.durationHours ?? 2,
          maxThreadCount: data.maxThreadCount ?? null
        });
      }
    } catch (err) {
      console.error('Failed to load prefill config:', err);
    } finally {
      setLoadingPrefillConfig(false);
    }
  };

  const updatePrefillConfig = async (enabledByDefault: boolean, durationHours: number, maxThreadCount?: number | null) => {
    if (authMode !== 'authenticated') return;
    try {
      setUpdatingPrefillConfig(true);
      const body: Record<string, unknown> = { enabledByDefault, durationHours };
      if (maxThreadCount !== undefined) {
        body.maxThreadCount = maxThreadCount;
      } else {
        body.maxThreadCount = prefillConfig.maxThreadCount;
      }
      const response = await fetch('/api/auth/guest/prefill/config', ApiService.getFetchOptions({
        method: 'POST',
        headers: {
          'Content-Type': 'application/json'
        },
        body: JSON.stringify(body)
      }));

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

  useEffect(() => {
    loadDefaultGuestPreferences();
    loadPrefillConfig();

    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    on('GuestPrefillConfigChanged', handlePrefillConfigChanged);

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
      off('GuestPrefillConfigChanged', handlePrefillConfigChanged);
    };
  }, [on, off, handleDefaultGuestPreferencesChanged, handleAllowedTimeFormatsChanged, handlePrefillConfigChanged]);

  return (
    <Card padding="none">
      {/* Header */}
      <div className="p-4 sm:p-5 border-b border-themed-secondary">
        <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
          <Settings2 className="w-5 h-5 text-themed-accent" />
          Guest Defaults
        </h3>
        <p className="text-sm mt-1 text-themed-muted">
          These settings apply to newly created guest sessions only. Existing sessions are not affected.
        </p>
      </div>

      <div className="p-4 sm:p-5 space-y-4">
        {/* Session & Access */}
        <div className="rounded-lg bg-themed-secondary p-4 space-y-4">
          <div className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
            Session &amp; Access
          </div>

          <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
            <div className="toggle-row-label whitespace-nowrap">{t('user.guest.sections.sessionDuration')}</div>
            <div className="flex items-center gap-2">
              <EnhancedDropdown
                options={translatedDurationOptions}
                value={guestDurationHours.toString()}
                onChange={(value) => onDurationChange(Number(value))}
                disabled={updatingDuration}
                className="w-48"
              />
              {updatingDuration && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
            </div>
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !loadingPrefillConfig &&
              !updatingPrefillConfig &&
              updatePrefillConfig(!prefillConfig.enabledByDefault, prefillConfig.durationHours)
            }
          >
            <div>
              <div className="toggle-row-label flex items-center gap-1.5">
                <Download className="w-3.5 h-3.5 text-themed-accent" />
                {t('user.guest.prefill.enableByDefault.label')}
              </div>
              <div className="toggle-row-description">
                {t('user.guest.prefill.enableByDefault.description')}
              </div>
            </div>
            <div className="flex items-center gap-2">
              {updatingPrefillConfig && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${prefillConfig.enabledByDefault ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>

          {prefillConfig.enabledByDefault && (
            <div className="flex items-start gap-2 p-3 rounded-md text-sm bg-themed-warning border border-themed-warning text-themed-warning">
              <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
              <span>{t('user.guest.prefill.warning')}</span>
            </div>
          )}

          <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
            <div className="toggle-row-label whitespace-nowrap">{t('user.guest.prefill.duration.label')}</div>
            <div className="flex items-center gap-2">
              <EnhancedDropdown
                options={prefillDurationOptions}
                value={prefillConfig.durationHours.toString()}
                onChange={(value) =>
                  updatePrefillConfig(prefillConfig.enabledByDefault, Number(value))
                }
                disabled={updatingPrefillConfig || loadingPrefillConfig}
                className="w-48"
              />
            </div>
          </div>

          <div className="flex flex-col sm:flex-row sm:items-center gap-2 sm:gap-4">
            <div className="toggle-row-label whitespace-nowrap flex items-center gap-1.5">
              <Network className="w-3.5 h-3.5 text-themed-accent" />
              {t('user.guest.prefill.maxThreads.label')}
            </div>
            <div className="flex items-center gap-2">
              <EnhancedDropdown
                options={maxThreadOptions}
                value={prefillConfig.maxThreadCount != null ? String(prefillConfig.maxThreadCount) : ''}
                onChange={(value) => {
                  const newValue = value === '' ? null : Number(value);
                  updatePrefillConfig(prefillConfig.enabledByDefault, prefillConfig.durationHours, newValue);
                }}
                disabled={updatingPrefillConfig || loadingPrefillConfig}
                className="w-48"
              />
              {updatingPrefillConfig && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
            </div>
          </div>
        </div>

        {/* Appearance */}
        <div className="rounded-lg bg-themed-secondary p-4 space-y-4">
          <div className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
            Appearance
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <div className="space-y-1.5">
              <div className="toggle-row-label">{t('user.guest.sections.defaultTheme')}</div>
              <div className="flex items-center gap-2">
                <EnhancedDropdown
                  options={availableThemes.map((theme) => ({
                    value: theme.id,
                    label: theme.name
                  }))}
                  value={defaultGuestTheme}
                  onChange={onGuestThemeChange}
                  disabled={updatingGuestTheme}
                  className="w-full"
                />
                {updatingGuestTheme && (
                  <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
                )}
              </div>
            </div>

            <div className="space-y-1.5">
              <div className="toggle-row-label">{t('user.guest.sections.refreshRate')}</div>
              <div className="flex items-center gap-2">
                <EnhancedDropdown
                  options={translatedRefreshRateOptions}
                  value={defaultGuestRefreshRate}
                  onChange={onGuestRefreshRateChange}
                  disabled={updatingGuestRefreshRate}
                  className="w-full"
                />
                {updatingGuestRefreshRate && (
                  <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
                )}
              </div>
            </div>
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !updatingGuestRefreshRateLock &&
              onGuestRefreshRateLockChange(!guestRefreshRateLocked)
            }
          >
            <div>
              <div className="toggle-row-label flex items-center gap-1.5">
                {guestRefreshRateLocked ? (
                  <Lock className="w-3.5 h-3.5 text-themed-accent" />
                ) : (
                  <Unlock className="w-3.5 h-3.5 text-themed-accent" />
                )}
                Lock Refresh Rate
              </div>
              <div className="toggle-row-description">
                When locked, guests cannot change their refresh rate
              </div>
            </div>
            <div className="flex items-center gap-2">
              {updatingGuestRefreshRateLock && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${guestRefreshRateLocked ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>
        </div>

        {/* Date & Time */}
        <div className="rounded-lg bg-themed-secondary p-4 space-y-4">
          <div className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
            {t('user.guest.sections.dateTime')}
          </div>

          <div className="space-y-1.5">
            <div className="toggle-row-label">{t('user.guest.timeFormats.title')}</div>
            <div className="relative">
              <MultiSelectDropdown
                options={timeFormatOptions.map((opt) => ({
                  value: opt.value,
                  label: opt.label,
                  description: opt.description,
                  icon: opt.icon
                }))}
                values={defaultGuestPreferences.allowedTimeFormats}
                onChange={handleAllowedFormatsChange}
                placeholder={t('user.guest.timeFormats.placeholder')}
                minSelections={1}
                disabled={updatingAllowedFormats || loadingDefaultPrefs}
                dropdownWidth="w-80"
              />
              {updatingAllowedFormats && (
                <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
              )}
            </div>
            <div className="toggle-row-description">
              {t('user.guest.timeFormats.note')}
            </div>
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !loadingDefaultPrefs &&
              handleUpdateDefaultGuestPref(
                'showYearInDates',
                !defaultGuestPreferences.showYearInDates
              )
            }
          >
            <div>
              <div className="toggle-row-label">{t('user.guest.preferences.showYear.label')}</div>
              <div className="toggle-row-description">{t('user.guest.preferences.showYear.description')}</div>
            </div>
            <div className="flex items-center gap-2">
              {updatingDefaultPref === 'showYearInDates' && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${defaultGuestPreferences.showYearInDates ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>
        </div>

        {/* Display */}
        <div className="rounded-lg bg-themed-secondary p-4 space-y-4">
          <div className="text-xs font-semibold uppercase tracking-wider text-themed-muted">
            {t('user.guest.sections.display')}
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !loadingDefaultPrefs &&
              handleUpdateDefaultGuestPref('sharpCorners', !defaultGuestPreferences.sharpCorners)
            }
          >
            <div>
              <div className="toggle-row-label">{t('user.guest.preferences.sharpCorners.label')}</div>
              <div className="toggle-row-description">{t('user.guest.preferences.sharpCorners.description')}</div>
            </div>
            <div className="flex items-center gap-2">
              {updatingDefaultPref === 'sharpCorners' && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${defaultGuestPreferences.sharpCorners ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !loadingDefaultPrefs &&
              handleUpdateDefaultGuestPref('disableTooltips', !defaultGuestPreferences.disableTooltips)
            }
          >
            <div>
              <div className="toggle-row-label">{t('user.guest.preferences.disableTooltips.label')}</div>
              <div className="toggle-row-description">{t('user.guest.preferences.disableTooltips.description')}</div>
            </div>
            <div className="flex items-center gap-2">
              {updatingDefaultPref === 'disableTooltips' && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${defaultGuestPreferences.disableTooltips ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>

          <div
            className="toggle-row cursor-pointer"
            onClick={() =>
              !loadingDefaultPrefs &&
              handleUpdateDefaultGuestPref('showDatasourceLabels', !defaultGuestPreferences.showDatasourceLabels)
            }
          >
            <div>
              <div className="toggle-row-label">{t('user.guest.preferences.datasourceLabels.label')}</div>
              <div className="toggle-row-description">{t('user.guest.preferences.datasourceLabels.description')}</div>
            </div>
            <div className="flex items-center gap-2">
              {updatingDefaultPref === 'showDatasourceLabels' && (
                <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
              )}
              <div className={`modern-toggle ${defaultGuestPreferences.showDatasourceLabels ? 'checked' : ''}`}>
                <span className="toggle-thumb" />
              </div>
            </div>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default GuestConfiguration;
