import React, { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Palette, Loader2, Lock, Unlock, Globe, MapPin, Monitor } from 'lucide-react';
import { Card } from '@components/ui/Card';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { MultiSelectDropdown } from '@components/ui/MultiSelectDropdown';
import { AccordionSection } from '@components/ui/AccordionSection';

interface AppearanceDisplayCardProps {
  // Theme
  defaultGuestTheme: string;
  onGuestThemeChange: (themeId: string) => void;
  updatingGuestTheme: boolean;
  availableThemes: Array<{ id: string; name: string }>;
  // Refresh rate
  defaultGuestRefreshRate: string;
  onGuestRefreshRateChange: (rate: string) => void;
  updatingGuestRefreshRate: boolean;
  guestRefreshRateLocked: boolean;
  onGuestRefreshRateLockChange: (locked: boolean) => void;
  updatingGuestRefreshRateLock: boolean;
  refreshRateOptions: Array<{ value: string; label: string }>;
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

  const handleRefreshRateLockToggle = () => {
    if (!updatingGuestRefreshRateLock) {
      onGuestRefreshRateLockChange(!guestRefreshRateLocked);
    }
  };

  const handleShowYearToggle = () => {
    if (!loadingDefaultPrefs) {
      onUpdateDefaultPref('showYearInDates', !defaultGuestPreferences.showYearInDates);
    }
  };

  const handleSharpCornersToggle = () => {
    if (!loadingDefaultPrefs) {
      onUpdateDefaultPref('sharpCorners', !defaultGuestPreferences.sharpCorners);
    }
  };

  const handleDisableTooltipsToggle = () => {
    if (!loadingDefaultPrefs) {
      onUpdateDefaultPref('disableTooltips', !defaultGuestPreferences.disableTooltips);
    }
  };

  const handleDatasourceLabelsToggle = () => {
    if (!loadingDefaultPrefs) {
      onUpdateDefaultPref('showDatasourceLabels', !defaultGuestPreferences.showDatasourceLabels);
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
        <p className="text-sm mt-1 text-themed-muted">
          {t('user.guest.sections.appearanceDisplaySubtitle')}
        </p>
      </div>

      <div className="p-4 sm:p-5 space-y-4">
        {/* Two-column grid: Appearance + Date & Time */}
        <div className="grid gap-4 sm:grid-cols-2">
          {/* Left column: Appearance */}
          <div className="settings-group settings-group--look">
            <div className="config-section-title">
              {t('user.guest.sections.appearance')}
            </div>

            {/* Default Theme dropdown */}
            <div className="space-y-1.5">
              <div className="toggle-row-label">{t('user.guest.sections.defaultTheme')}</div>
              <div className="relative">
                <EnhancedDropdown
                  options={themeOptions}
                  value={defaultGuestTheme}
                  onChange={onGuestThemeChange}
                  disabled={updatingGuestTheme}
                  className="w-full"
                />
                {updatingGuestTheme && (
                  <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
                )}
              </div>
            </div>

            {/* Refresh Rate dropdown */}
            <div className="space-y-1.5">
              <div className="toggle-row-label">{t('user.guest.sections.refreshRate')}</div>
              <div className="relative">
                <EnhancedDropdown
                  options={refreshRateOptions}
                  value={defaultGuestRefreshRate}
                  onChange={onGuestRefreshRateChange}
                  disabled={updatingGuestRefreshRate}
                  className="w-full"
                />
                {updatingGuestRefreshRate && (
                  <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
                )}
              </div>
            </div>

            {/* Lock Refresh Rate toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={handleRefreshRateLockToggle}
            >
              <div>
                <div className="toggle-row-label flex items-center gap-1.5">
                  {guestRefreshRateLocked ? (
                    <Lock className="w-3.5 h-3.5 text-themed-accent" />
                  ) : (
                    <Unlock className="w-3.5 h-3.5 text-themed-accent" />
                  )}
                  {t('user.guest.preferences.lockRefreshRate.label')}
                </div>
                <div className="toggle-row-description">
                  {t('user.guest.preferences.lockRefreshRate.description')}
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

          {/* Right column: Date & Time */}
          <div className="settings-group settings-group--look">
            <div className="config-section-title">
              {t('user.guest.sections.dateTime')}
            </div>

            {/* Allowed Time Formats multi-select */}
            <div className="space-y-1.5">
              <div className="toggle-row-label">{t('user.guest.timeFormats.title')}</div>
              <div className="relative">
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
                  <Loader2 className="w-4 h-4 animate-spin absolute right-10 top-1/2 -translate-y-1/2 text-themed-accent" />
                )}
              </div>
              <div className="toggle-row-description">
                {t('user.guest.timeFormats.note')}
              </div>
            </div>

            {/* Show Year in Dates toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={handleShowYearToggle}
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
        </div>

        {/* Display Toggles - Accordion (collapsed by default) */}
        <AccordionSection
          title={t('user.guest.sections.display')}
          icon={Monitor}
          isExpanded={displayTogglesExpanded}
          onToggle={handleDisplayTogglesToggle}
        >
          <div className="settings-group settings-group--display">
            {/* Sharp Corners toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={handleSharpCornersToggle}
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

            {/* Disable Tooltips toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={handleDisableTooltipsToggle}
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

            {/* Show Datasource Labels toggle */}
            <div
              className="toggle-row cursor-pointer"
              onClick={handleDatasourceLabelsToggle}
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
        </AccordionSection>
      </div>
    </Card>
  );
};

export default AppearanceDisplayCard;
