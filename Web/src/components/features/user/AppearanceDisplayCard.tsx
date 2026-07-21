import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Palette, Lock, Unlock, Globe, MapPin, Monitor } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { AccordionSection } from '@components/ui/AccordionSection';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import LoadingSpinner from '@components/common/LoadingSpinner';

interface AppearanceDisplayCardProps {
  // Theme
  defaultGuestTheme: string;
  onGuestThemeChange: (themeId: string) => void;
  updatingGuestTheme: boolean;
  availableThemes: { id: string; name: string }[];
  // Refresh rate
  defaultGuestRefreshRate: string;
  onGuestRefreshRateChange: (rate: string) => void;
  updatingGuestRefreshRate: boolean;
  guestRefreshRateLocked: boolean;
  onGuestRefreshRateLockChange: (locked: boolean) => void;
  updatingGuestRefreshRateLock: boolean;
  refreshRateOptions: { value: string; label: string }[];
  // Date & Time
  defaultGuestPreferences: {
    useLocalTimezone: boolean;
    use24HourFormat: boolean;
    showYearInDates: boolean;
    sharpCorners: boolean;
    disableTooltips: boolean;
    showDatasourceLabels: boolean;
    allowedTimeFormats: string[];
  };
  onUpdateDefaultPref: (key: string, value: boolean) => void;
  updatingDefaultPref: string | null;
  loadingDefaultPrefs: boolean;
  // Time formats
  onAllowedFormatsChange: (formats: string[]) => void;
  updatingAllowedFormats: boolean;
}

type PrefKey = keyof AppearanceDisplayCardProps['defaultGuestPreferences'];

const AppearanceDisplayCard: React.FC<AppearanceDisplayCardProps> = ({
  defaultGuestTheme,
  onGuestThemeChange,
  updatingGuestTheme,
  availableThemes,
  defaultGuestRefreshRate,
  onGuestRefreshRateChange,
  updatingGuestRefreshRate,
  guestRefreshRateLocked,
  onGuestRefreshRateLockChange,
  updatingGuestRefreshRateLock,
  refreshRateOptions,
  defaultGuestPreferences,
  onUpdateDefaultPref,
  updatingDefaultPref,
  loadingDefaultPrefs,
  onAllowedFormatsChange,
  updatingAllowedFormats
}) => {
  const { t } = useTranslation();
  const [displayTogglesExpanded, setDisplayTogglesExpanded] = useState(false);

  const themeOptions = availableThemes.map((theme: { id: string; name: string }) => ({
    value: theme.id,
    label: theme.name
  }));

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

  const onOffOptions = (): [
    { value: string; label: string },
    { value: string; label: string; activeColor: 'success' }
  ] => [
    { value: 'off', label: t('common.off') },
    { value: 'on', label: t('common.on'), activeColor: 'success' }
  ];

  const handleRefreshRateLockChange = (value: string) => {
    onGuestRefreshRateLockChange(value === 'on');
  };

  const handlePrefToggleChange = (key: PrefKey, value: string) => {
    if (!loadingDefaultPrefs) {
      onUpdateDefaultPref(key, value === 'on');
    }
  };

  const handleDisplayTogglesToggle = () => {
    setDisplayTogglesExpanded((prev: boolean) => !prev);
  };

  return (
    <Card padding="none">
      {/* Header */}
      <div className="p-4 sm:p-5 border-b border-themed-secondary">
        <h3 className="text-lg font-semibold flex items-center gap-2 text-themed-primary">
          <Palette className="w-5 h-5 text-themed-accent" />
          {t('user.guest.sections.appearanceDisplay')}
        </h3>
      </div>

      <div className="p-4 sm:p-5 space-y-4">
        {/* Two-column grid: Appearance + Date & Time */}
        <div className="grid gap-4 sm:grid-cols-2">
          {/* Left column: Appearance */}
          <div className="user-settings-group">
            <p className="mgmt-subhead">{t('user.guest.sections.appearance')}</p>
            <div className="mgmt-list divided-list user-settings-list">
              <div className="mgmt-row">
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title">{t('user.guest.sections.defaultTheme')}</p>
                </div>
                <div className="mgmt-row__actions">
                  <span className="user-settings-dropdown">
                    <EnhancedDropdown
                      options={themeOptions}
                      value={defaultGuestTheme}
                      onChange={onGuestThemeChange}
                      disabled={updatingGuestTheme}
                      size="md"
                      className="w-40"
                    />
                    {updatingGuestTheme && (
                      <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
                    )}
                  </span>
                </div>
              </div>

              <div className="mgmt-row">
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title">{t('user.guest.sections.refreshRate')}</p>
                </div>
                <div className="mgmt-row__actions">
                  <span className="user-settings-dropdown">
                    <EnhancedDropdown
                      options={refreshRateOptions}
                      value={defaultGuestRefreshRate}
                      onChange={onGuestRefreshRateChange}
                      disabled={updatingGuestRefreshRate}
                      size="md"
                      className="w-40"
                    />
                    {updatingGuestRefreshRate && (
                      <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
                    )}
                  </span>
                </div>
              </div>

              <div className="mgmt-row">
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title flex items-center gap-1.5">
                    {guestRefreshRateLocked ? (
                      <Lock className="w-3.5 h-3.5 text-themed-accent" />
                    ) : (
                      <Unlock className="w-3.5 h-3.5 text-themed-accent" />
                    )}
                    {t('user.guest.preferences.lockRefreshRate.label')}
                  </p>
                  <p className="mgmt-row__meta">
                    {t('user.guest.preferences.lockRefreshRate.description')}
                  </p>
                </div>
                <div className="mgmt-row__actions">
                  <ToggleSwitch
                    options={onOffOptions()}
                    value={guestRefreshRateLocked ? 'on' : 'off'}
                    onChange={handleRefreshRateLockChange}
                    loading={updatingGuestRefreshRateLock}
                    size="sm"
                  />
                </div>
              </div>
            </div>
          </div>

          {/* Right column: Date & Time */}
          <div className="user-settings-group">
            <p className="mgmt-subhead">{t('user.guest.sections.dateTime')}</p>
            <div className="mgmt-list divided-list user-settings-list">
              <div className="mgmt-row">
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title">{t('user.guest.timeFormats.title')}</p>
                  <p className="mgmt-row__meta">{t('user.guest.timeFormats.note')}</p>
                </div>
                <div className="mgmt-row__actions">
                  <span className="user-settings-dropdown">
                    <MultiSelectDropdown
                      options={timeFormatOptions}
                      values={defaultGuestPreferences.allowedTimeFormats}
                      onChange={onAllowedFormatsChange}
                      placeholder={t('user.guest.timeFormats.placeholder')}
                      minSelections={1}
                      disabled={updatingAllowedFormats || loadingDefaultPrefs}
                      dropdownWidth="w-80"
                    />
                    {updatingAllowedFormats && (
                      <LoadingSpinner inline size="sm" className="user-settings-inline-spinner" />
                    )}
                  </span>
                </div>
              </div>

              <div className="mgmt-row">
                <div className="mgmt-row__body">
                  <p className="mgmt-row__title">{t('user.guest.preferences.showYear.label')}</p>
                  <p className="mgmt-row__meta">
                    {t('user.guest.preferences.showYear.description')}
                  </p>
                </div>
                <div className="mgmt-row__actions">
                  <ToggleSwitch
                    options={onOffOptions()}
                    value={defaultGuestPreferences.showYearInDates ? 'on' : 'off'}
                    onChange={(value) => handlePrefToggleChange('showYearInDates', value)}
                    disabled={loadingDefaultPrefs}
                    loading={updatingDefaultPref === 'showYearInDates'}
                    size="sm"
                  />
                </div>
              </div>
            </div>
          </div>
        </div>

        {/* Display Toggles - Accordion (collapsed by default) */}
        <AccordionSection
          title={t('user.guest.sections.display')}
          icon={Monitor}
          isExpanded={displayTogglesExpanded}
          onToggle={handleDisplayTogglesToggle}
        >
          <div className="mgmt-list divided-list user-settings-list">
            <div className="mgmt-row">
              <div className="mgmt-row__body">
                <p className="mgmt-row__title">{t('user.guest.preferences.sharpCorners.label')}</p>
                <p className="mgmt-row__meta">
                  {t('user.guest.preferences.sharpCorners.description')}
                </p>
              </div>
              <div className="mgmt-row__actions">
                <ToggleSwitch
                  options={onOffOptions()}
                  value={defaultGuestPreferences.sharpCorners ? 'on' : 'off'}
                  onChange={(value) => handlePrefToggleChange('sharpCorners', value)}
                  disabled={loadingDefaultPrefs}
                  loading={updatingDefaultPref === 'sharpCorners'}
                  size="sm"
                />
              </div>
            </div>

            <div className="mgmt-row">
              <div className="mgmt-row__body">
                <p className="mgmt-row__title">
                  {t('user.guest.preferences.disableTooltips.label')}
                </p>
                <p className="mgmt-row__meta">
                  {t('user.guest.preferences.disableTooltips.description')}
                </p>
              </div>
              <div className="mgmt-row__actions">
                <ToggleSwitch
                  options={onOffOptions()}
                  value={defaultGuestPreferences.disableTooltips ? 'on' : 'off'}
                  onChange={(value) => handlePrefToggleChange('disableTooltips', value)}
                  disabled={loadingDefaultPrefs}
                  loading={updatingDefaultPref === 'disableTooltips'}
                  size="sm"
                />
              </div>
            </div>

            <div className="mgmt-row">
              <div className="mgmt-row__body">
                <p className="mgmt-row__title">
                  {t('user.guest.preferences.datasourceLabels.label')}
                </p>
                <p className="mgmt-row__meta">
                  {t('user.guest.preferences.datasourceLabels.description')}
                </p>
              </div>
              <div className="mgmt-row__actions">
                <ToggleSwitch
                  options={onOffOptions()}
                  value={defaultGuestPreferences.showDatasourceLabels ? 'on' : 'off'}
                  onChange={(value) => handlePrefToggleChange('showDatasourceLabels', value)}
                  disabled={loadingDefaultPrefs}
                  loading={updatingDefaultPref === 'showDatasourceLabels'}
                  size="sm"
                />
              </div>
            </div>
          </div>
        </AccordionSection>
      </div>
    </Card>
  );
};

export default AppearanceDisplayCard;
