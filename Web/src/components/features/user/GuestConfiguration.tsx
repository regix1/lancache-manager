import React, { useEffect, useState, useCallback } from 'react';
import { Settings2, Loader2, Globe, MapPin } from 'lucide-react';
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
  const [updatingTimeFormat, setUpdatingTimeFormat] = useState(false);
  const [updatingAllowedFormats, setUpdatingAllowedFormats] = useState(false);

  // Get current time format from the two boolean settings
  const getCurrentTimeFormat = (): TimeSettingValue => {
    const isLocal = defaultGuestPreferences.useLocalTimezone;
    const is24h = defaultGuestPreferences.use24HourFormat;
    if (isLocal && is24h) return 'local-24h';
    if (isLocal && !is24h) return 'local-12h';
    if (!isLocal && is24h) return 'server-24h';
    return 'server-12h';
  };

  // Handle time format dropdown change - updates both booleans
  const handleTimeFormatChange = async (value: string) => {
    const typedValue = value as TimeSettingValue;
    const newUseLocal = typedValue.startsWith('local');
    const newUse24Hour = typedValue.endsWith('24h');

    try {
      setUpdatingTimeFormat(true);

      // Update both settings
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
      } else {
        showToast('error', 'Failed to update default time format');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update default time format');
    } finally {
      setUpdatingTimeFormat(false);
    }
  };

  const timeFormatOptions = [
    {
      value: 'server-24h',
      label: 'Server (24h)',
      description: 'Server timezone, 24-hour format',
      icon: Globe
    },
    {
      value: 'server-12h',
      label: 'Server (12h)',
      description: 'Server timezone, 12-hour format',
      icon: Globe
    },
    {
      value: 'local-24h',
      label: 'Local (24h)',
      description: 'Guest\'s timezone, 24-hour format',
      icon: MapPin
    },
    {
      value: 'local-12h',
      label: 'Local (12h)',
      description: 'Guest\'s timezone, 12-hour format',
      icon: MapPin
    }
  ];

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
        showToast('error', errorData.error || `Failed to update default ${key}`);
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || `Failed to update default ${key}`);
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
        setDefaultGuestPreferences((prev) => ({
          ...prev,
          allowedTimeFormats: formats
        }));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update allowed time formats');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update allowed time formats');
    } finally {
      setUpdatingAllowedFormats(false);
    }
  };

  useEffect(() => {
    loadDefaultGuestPreferences();

    on('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
    on('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);

    return () => {
      off('DefaultGuestPreferencesChanged', handleDefaultGuestPreferencesChanged);
      off('AllowedTimeFormatsChanged', handleAllowedTimeFormatsChanged);
    };
  }, [on, off, handleDefaultGuestPreferencesChanged, handleAllowedTimeFormatsChanged]);

  return (
    <Card padding="none">
      <div
        className="p-4 sm:p-5 border-b"
        style={{ borderColor: 'var(--theme-border-secondary)' }}
      >
        <h3
          className="text-lg font-semibold flex items-center gap-2"
          style={{ color: 'var(--theme-text-primary)' }}
        >
          <Settings2 className="w-5 h-5" style={{ color: 'var(--theme-primary)' }} />
          Guest Configuration
        </h3>
        <p className="text-sm mt-1" style={{ color: 'var(--theme-text-muted)' }}>
          Configure default settings for guest sessions
        </p>
      </div>

      <div className="p-4 sm:p-5 space-y-5">
        {/* Core Settings */}
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
          <div className="config-section">
            <div className="config-section-title">Session Duration</div>
            <EnhancedDropdown
              options={durationOptions}
              value={guestDurationHours.toString()}
              onChange={(value) => onDurationChange(Number(value))}
              disabled={updatingDuration}
              className="w-full"
            />
            {updatingDuration && (
              <Loader2
                className="w-4 h-4 animate-spin mt-2"
                style={{ color: 'var(--theme-primary)' }}
              />
            )}
          </div>

          <div className="config-section">
            <div className="config-section-title">Default Theme</div>
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
              <Loader2
                className="w-4 h-4 animate-spin mt-2"
                style={{ color: 'var(--theme-primary)' }}
              />
            )}
          </div>

          <div className="config-section">
            <div className="config-section-title">Refresh Rate</div>
            <EnhancedDropdown
              options={refreshRateOptions}
              value={defaultGuestRefreshRate}
              onChange={onGuestRefreshRateChange}
              disabled={updatingGuestRefreshRate}
              className="w-full"
            />
            {updatingGuestRefreshRate && (
              <Loader2
                className="w-4 h-4 animate-spin mt-2"
                style={{ color: 'var(--theme-primary)' }}
              />
            )}
          </div>
        </div>

        {/* Preferences Grid */}
        <div className="grid gap-4 sm:grid-cols-2">
          {/* Date & Time */}
          <div className="config-section">
            <div className="config-section-title">Date & Time</div>
            <div className="space-y-3">
              {/* Default Time Format */}
              <div>
                <div className="toggle-row-label mb-1.5">Default Time Format</div>
                <EnhancedDropdown
                  options={timeFormatOptions}
                  value={getCurrentTimeFormat()}
                  onChange={handleTimeFormatChange}
                  disabled={updatingTimeFormat || loadingDefaultPrefs}
                  className="w-full"
                />
                {updatingTimeFormat && (
                  <Loader2
                    className="w-4 h-4 animate-spin mt-1"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>

              {/* Allowed Time Formats */}
              <div>
                <div className="toggle-row-label mb-1.5">Allowed Time Formats</div>
                <MultiSelectDropdown
                  options={timeFormatOptions.map((opt) => ({
                    value: opt.value,
                    label: opt.label,
                    description: opt.description,
                    icon: opt.icon
                  }))}
                  values={defaultGuestPreferences.allowedTimeFormats}
                  onChange={handleAllowedFormatsChange}
                  placeholder="Select allowed formats"
                  minSelections={1}
                  disabled={updatingAllowedFormats || loadingDefaultPrefs}
                />
                {updatingAllowedFormats && (
                  <Loader2
                    className="w-4 h-4 animate-spin mt-1"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
                <div className="toggle-row-description mt-1">
                  Time formats guests can choose from
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
                  <div className="toggle-row-label">Show Year</div>
                  <div className="toggle-row-description">Always include year in dates</div>
                </div>
                <div className="flex items-center gap-2">
                  {updatingDefaultPref === 'showYearInDates' && (
                    <Loader2
                      className="w-4 h-4 animate-spin"
                      style={{ color: 'var(--theme-primary)' }}
                    />
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
            <div className="config-section-title">Display</div>
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
                  <div className="toggle-row-label">Sharp Corners</div>
                  <div className="toggle-row-description">Square instead of rounded</div>
                </div>
                <div className="flex items-center gap-2">
                  {updatingDefaultPref === 'sharpCorners' && (
                    <Loader2
                      className="w-4 h-4 animate-spin"
                      style={{ color: 'var(--theme-primary)' }}
                    />
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
                  <div className="toggle-row-label">Disable Tooltips</div>
                  <div className="toggle-row-description">Hide tooltip hints</div>
                </div>
                <div className="flex items-center gap-2">
                  {updatingDefaultPref === 'disableTooltips' && (
                    <Loader2
                      className="w-4 h-4 animate-spin"
                      style={{ color: 'var(--theme-primary)' }}
                    />
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
                  <div className="toggle-row-label">Datasource Labels</div>
                  <div className="toggle-row-description">Show in multi-source mode</div>
                </div>
                <div className="flex items-center gap-2">
                  {updatingDefaultPref === 'showDatasourceLabels' && (
                    <Loader2
                      className="w-4 h-4 animate-spin"
                      style={{ color: 'var(--theme-primary)' }}
                    />
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
