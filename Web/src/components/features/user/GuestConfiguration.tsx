import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Settings2, Loader2, Globe, MapPin, Download, AlertTriangle } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import ApiService from '@services/api.service';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext';
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
  availableThemes
}) => {
  const { t } = useTranslation();
  const { on, off } = useSignalR();
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
    durationHours: 2
  });
  const [loadingPrefillConfig, setLoadingPrefillConfig] = useState(false);
  const [updatingPrefillConfig, setUpdatingPrefillConfig] = useState(false);

  // Helper to update default time format based on a format value
  const updateDefaultTimeFormat = async (format: TimeSettingValue) => {
    const newUseLocal = format.startsWith('local');
    const newUse24Hour = format.endsWith('24h');

    const [localResponse, formatResponse] = await Promise.all([
      fetch('/api/system/default-guest-preferences/useLocalTimezone', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...ApiService.getHeaders() },
        body: JSON.stringify({ value: newUseLocal })
      }),
      fetch('/api/system/default-guest-preferences/use24HourFormat', {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json', ...ApiService.getHeaders() },
        body: JSON.stringify({ value: newUse24Hour })
      })
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
  const preferenceLabels: Record<string, string> = {
    showYearInDates: t('user.guest.preferences.showYear.label'),
    sharpCorners: t('user.guest.preferences.sharpCorners.label'),
    disableTooltips: t('user.guest.preferences.disableTooltips.label'),
    showDatasourceLabels: t('user.guest.preferences.datasourceLabels.label')
  };

  const loadDefaultGuestPreferences = async () => {
    try {
      setLoadingDefaultPrefs(true);
      const response = await fetch('/api/system/default-guest-preferences', {
        headers: ApiService.getHeaders()
      });
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
    try {
      setUpdatingDefaultPref(key);
      const response = await fetch(`/api/system/default-guest-preferences/${key}`, {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ value })
      });

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

  const handleAllowedFormatsChange = async (formats: string[]) => {
    try {
      setUpdatingAllowedFormats(true);
      const response = await fetch('/api/system/default-guest-preferences/allowed-time-formats', {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ formats })
      });

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
      const response = await fetch('/api/auth/guest/prefill/config', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setPrefillConfig({
          enabledByDefault: data.enabledByDefault ?? false,
          durationHours: data.durationHours ?? 2
        });
      }
    } catch (err) {
      console.error('Failed to load prefill config:', err);
    } finally {
      setLoadingPrefillConfig(false);
    }
  };

  const updatePrefillConfig = async (enabledByDefault: boolean, durationHours: number) => {
    try {
      setUpdatingPrefillConfig(true);
      const response = await fetch('/api/auth/guest/prefill/config', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ enabledByDefault, durationHours })
      });

      if (response.ok) {
        const data = await response.json();
        setPrefillConfig({
          enabledByDefault: data.enabledByDefault,
          durationHours: data.durationHours
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

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    };
  }, [on, off, handleDefaultGuestPreferencesChanged, handleAllowedTimeFormatsChanged]);

  return (
    <Card padding="none">
      <div className="p-4 sm:p-5 border-b border-themed-secondary">
        <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
          <Settings2 className="w-5 h-5 text-themed-accent" />
          {t('user.guest.title')}
        </h3>
        <p className="text-sm mt-1 text-themed-muted">
          {t('user.guest.subtitle')}
        </p>
      </div>

      <div className="p-4 sm:p-5 space-y-5">
        {/* Core Settings */}
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <div className="config-section">
            <div className="config-section-title">{t('user.guest.sections.sessionDuration')}</div>
            <EnhancedDropdown
              options={translatedDurationOptions}
              value={guestDurationHours.toString()}
              onChange={(value) => onDurationChange(Number(value))}
              disabled={updatingDuration}
              className="w-full"
            />
            {updatingDuration && (
              <Loader2 className="w-4 h-4 animate-spin mt-2 text-themed-accent" />
            )}
          </div>

          <div className="config-section">
            <div className="config-section-title">{t('user.guest.sections.defaultTheme')}</div>
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
              <Loader2 className="w-4 h-4 animate-spin mt-2 text-themed-accent" />
            )}
          </div>

          <div className="config-section">
            <div className="config-section-title">{t('user.guest.sections.refreshRate')}</div>
            <EnhancedDropdown
              options={translatedRefreshRateOptions}
              value={defaultGuestRefreshRate}
              onChange={onGuestRefreshRateChange}
              disabled={updatingGuestRefreshRate}
              className="w-full"
            />
            {updatingGuestRefreshRate && (
              <Loader2 className="w-4 h-4 animate-spin mt-2 text-themed-accent" />
            )}
          </div>
        </div>

        {/* Prefill Permissions */}
        <div className="config-section">
          <div className="config-section-title flex items-center gap-2">
            <Download className="w-4 h-4 text-themed-accent" />
            {t('user.guest.prefill.title')}
          </div>
          <p className="text-sm mb-3 text-themed-muted">
            {t('user.guest.prefill.subtitle')}
          </p>
          <div className="space-y-3">
            {/* Default Enabled Toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={() =>
                !loadingPrefillConfig &&
                !updatingPrefillConfig &&
                updatePrefillConfig(!prefillConfig.enabledByDefault, prefillConfig.durationHours)
              }
            >
              <div>
                <div className="toggle-row-label">{t('user.guest.prefill.enableByDefault.label')}</div>
                <div className="toggle-row-description">
                  {t('user.guest.prefill.enableByDefault.description')}
                </div>
              </div>
              <div className="flex items-center gap-2">
                {updatingPrefillConfig && (
                  <Loader2 className="w-4 h-4 animate-spin text-themed-accent" />
                )}
                <div
                  className={`modern-toggle ${prefillConfig.enabledByDefault ? 'checked' : ''}`}
                >
                  <span className="toggle-thumb" />
                </div>
              </div>
            </div>

            {/* Warning for enabled by default */}
            {prefillConfig.enabledByDefault && (
              <div className="flex items-start gap-2 p-3 rounded-md text-sm bg-themed-warning border border-themed-warning text-themed-warning">
                <AlertTriangle className="w-4 h-4 flex-shrink-0 mt-0.5" />
                <span>
                  {t('user.guest.prefill.warning')}
                </span>
              </div>
            )}

            {/* Duration Dropdown */}
            <div>
              <div className="toggle-row-label mb-1.5">{t('user.guest.prefill.duration.label')}</div>
              <EnhancedDropdown
                options={prefillDurationOptions}
                value={prefillConfig.durationHours.toString()}
                onChange={(value) =>
                  updatePrefillConfig(prefillConfig.enabledByDefault, Number(value))
                }
                disabled={updatingPrefillConfig || loadingPrefillConfig}
                className="w-48"
              />
              <div className="toggle-row-description mt-1">
                {t('user.guest.prefill.duration.description')}
              </div>
            </div>
          </div>
        </div>

        {/* Preferences Grid */}
        <div className="grid gap-4 sm:grid-cols-2">
          {/* Date & Time */}
          <div className="config-section">
            <div className="config-section-title">{t('user.guest.sections.dateTime')}</div>
            <div className="space-y-3">
              {/* Allowed Time Formats */}
              <div>
                <div className="toggle-row-label mb-1.5">{t('user.guest.timeFormats.title')}</div>
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
                <div className="toggle-row-description mt-1">
                  {t('user.guest.timeFormats.note')}
                </div>
              </div>

              {/* Show Year Toggle */}
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
                  <div
                    className={`modern-toggle ${defaultGuestPreferences.showYearInDates ? 'checked' : ''}`}
                  >
                    <span className="toggle-thumb" />
                  </div>
                </div>
              </div>
            </div>
          </div>

          {/* Display */}
          <div className="config-section">
            <div className="config-section-title">{t('user.guest.sections.display')}</div>
            <div className="space-y-1">
              <div
                className="toggle-row cursor-pointer"
                onClick={() =>
                  !loadingDefaultPrefs &&
                  handleUpdateDefaultGuestPref(
                    'sharpCorners',
                    !defaultGuestPreferences.sharpCorners
                  )
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
                  <div
                    className={`modern-toggle ${defaultGuestPreferences.sharpCorners ? 'checked' : ''}`}
                  >
                    <span className="toggle-thumb" />
                  </div>
                </div>
              </div>
              <div
                className="toggle-row cursor-pointer"
                onClick={() =>
                  !loadingDefaultPrefs &&
                  handleUpdateDefaultGuestPref(
                    'disableTooltips',
                    !defaultGuestPreferences.disableTooltips
                  )
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
                  <div
                    className={`modern-toggle ${defaultGuestPreferences.disableTooltips ? 'checked' : ''}`}
                  >
                    <span className="toggle-thumb" />
                  </div>
                </div>
              </div>
              <div
                className="toggle-row cursor-pointer"
                onClick={() =>
                  !loadingDefaultPrefs &&
                  handleUpdateDefaultGuestPref(
                    'showDatasourceLabels',
                    !defaultGuestPreferences.showDatasourceLabels
                  )
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
                  <div
                    className={`modern-toggle ${defaultGuestPreferences.showDatasourceLabels ? 'checked' : ''}`}
                  >
                    <span className="toggle-thumb" />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </Card>
  );
};

export default GuestConfiguration;
