import React, { useState, useEffect, useCallback } from 'react';
import { Brush, Bell, Database, Calendar } from 'lucide-react';
import { Checkbox } from '@components/ui/Checkbox';
import preferencesService from '@services/preferences.service';
import themeService from '@services/theme.service';
import { setGlobalAlwaysShowYearPreference } from '@utils/yearDisplayPreference';

interface PreferenceRowProps {
  label: string;
  description: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
}

const PreferenceRow: React.FC<PreferenceRowProps> = ({
  label,
  description,
  checked,
  onChange,
  disabled = false
}) => (
  <div className="flex items-start gap-3 py-2">
    <div className="pt-0.5">
      <Checkbox
        checked={checked}
        onChange={(e) => onChange(e.target.checked)}
        disabled={disabled}
      />
    </div>
    <div className="flex-1 min-w-0">
      <p className="text-sm font-medium text-themed-primary">{label}</p>
      <p className="text-xs mt-0.5 text-themed-muted">{description}</p>
    </div>
  </div>
);

interface PreferenceSectionProps {
  icon: React.ElementType;
  title: string;
  iconBgVar: string;
  iconColorVar: string;
  children: React.ReactNode;
}

const PreferenceSection: React.FC<PreferenceSectionProps> = ({
  icon: Icon,
  title,
  iconBgVar,
  iconColorVar,
  children
}) => (
  <div className="p-4 rounded-lg bg-themed-tertiary">
    <div className="flex items-center gap-2 mb-3 pb-2 border-b border-themed-secondary">
      <div
        className="w-6 h-6 rounded flex items-center justify-center"
        style={{ backgroundColor: `color-mix(in srgb, var(${iconBgVar}) 15%, transparent)` }}
      >
        <Icon className="w-3.5 h-3.5" style={{ color: `var(${iconColorVar})` }} />
      </div>
      <h4 className="text-sm font-semibold text-themed-secondary">{title}</h4>
    </div>
    <div className="space-y-1">{children}</div>
  </div>
);

const DisplayPreferences: React.FC = () => {

  // Visual preferences
  const [sharpCorners, setSharpCorners] = useState(false);
  const [disableTooltips, setDisableTooltips] = useState(false);

  // Notification preferences
  const [disableStickyNotifications, setDisableStickyNotifications] = useState(false);
  const [picsAlwaysVisible, setPicsAlwaysVisible] = useState(false);

  // Downloads preferences
  const [showDatasourceLabels, setShowDatasourceLabels] = useState(true);

  // Date & Time preferences
  const [alwaysShowYear, setAlwaysShowYear] = useState(false);

  const [isLoading, setIsLoading] = useState(true);

  // Load initial preferences
  useEffect(() => {
    const loadPreferences = async () => {
      try {
        const prefs = await preferencesService.getPreferences();
        setSharpCorners(prefs.sharpCorners);
        setDisableTooltips(prefs.disableTooltips);
        setDisableStickyNotifications(prefs.disableStickyNotifications);
        setPicsAlwaysVisible(prefs.picsAlwaysVisible);
        setShowDatasourceLabels(prefs.showDatasourceLabels);
        setAlwaysShowYear(prefs.showYearInDates);
      } catch (error) {
        console.error('[DisplayPreferences] Failed to load preferences:', error);
      } finally {
        setIsLoading(false);
      }
    };

    loadPreferences();
  }, []);

  // Listen for preference changes from SignalR
  useEffect(() => {
    const handlePreferenceChange = (event: Event) => {
      const { key, value } = (event as CustomEvent<{ key: string; value: boolean }>).detail;

      switch (key) {
        case 'sharpCorners':
          setSharpCorners(value);
          break;
        case 'disableTooltips':
          setDisableTooltips(value);
          break;
        case 'disableStickyNotifications':
          setDisableStickyNotifications(value);
          break;
        case 'picsAlwaysVisible':
          setPicsAlwaysVisible(value);
          break;
        case 'showDatasourceLabels':
          setShowDatasourceLabels(value);
          break;
        case 'showYearInDates':
          setAlwaysShowYear(value);
          break;
      }
    };

    window.addEventListener('preference-changed', handlePreferenceChange);
    return () => window.removeEventListener('preference-changed', handlePreferenceChange);
  }, []);

  // Handlers for each preference
  const handleSharpCornersChange = useCallback(async (checked: boolean) => {
    setSharpCorners(checked);
    await themeService.setSharpCorners(checked);
  }, []);

  const handleTooltipsChange = useCallback(async (checked: boolean) => {
    setDisableTooltips(checked);
    await themeService.setDisableTooltips(checked);
  }, []);

  const handleStickyNotificationsChange = useCallback(async (checked: boolean) => {
    setDisableStickyNotifications(checked);
    await themeService.setDisableStickyNotifications(checked);
  }, []);

  const handlePicsVisibleChange = useCallback(async (checked: boolean) => {
    setPicsAlwaysVisible(checked);
    await themeService.setPicsAlwaysVisible(checked);
  }, []);

  const handleDatasourceLabelsChange = useCallback(async (checked: boolean) => {
    setShowDatasourceLabels(checked);
    await preferencesService.setPreference('showDatasourceLabels', checked);
  }, []);

  const handleAlwaysShowYearChange = useCallback(async (checked: boolean) => {
    setAlwaysShowYear(checked);
    // Update global state immediately for instant UI feedback
    setGlobalAlwaysShowYearPreference(checked);
    // Save to API (will also dispatch preference-changed event for TimezoneContext)
    await preferencesService.setPreference('showYearInDates', checked);
  }, []);

  if (isLoading) {
    return (
      <div className="animate-pulse grid grid-cols-1 md:grid-cols-2 gap-4">
        <div className="h-32 rounded-lg bg-themed-tertiary" />
        <div className="h-32 rounded-lg bg-themed-tertiary" />
        <div className="h-24 rounded-lg bg-themed-tertiary" />
        <div className="h-24 rounded-lg bg-themed-tertiary" />
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
      {/* Visual Settings */}
      <PreferenceSection
        icon={Brush}
        title="Visual"
        iconBgVar="--theme-icon-purple"
        iconColorVar="--theme-icon-purple"
      >
        <PreferenceRow
          label="Sharp Corners"
          description="Use square corners instead of rounded"
          checked={sharpCorners}
          onChange={handleSharpCornersChange}
        />
        <PreferenceRow
          label="Disable Tooltips"
          description="Hide tooltip hints throughout the UI"
          checked={disableTooltips}
          onChange={handleTooltipsChange}
        />
      </PreferenceSection>

      {/* Notification Settings */}
      <PreferenceSection
        icon={Bell}
        title="Notifications"
        iconBgVar="--theme-icon-orange"
        iconColorVar="--theme-icon-orange"
      >
        <PreferenceRow
          label="Disable Sticky Notifications"
          description="Auto-dismiss all notifications"
          checked={disableStickyNotifications}
          onChange={handleStickyNotificationsChange}
        />
        <PreferenceRow
          label="Keep Processing Visible"
          description="Always show PICS download notifications"
          checked={picsAlwaysVisible}
          onChange={handlePicsVisibleChange}
        />
      </PreferenceSection>

      {/* Downloads Settings */}
      <PreferenceSection
        icon={Database}
        title="Downloads"
        iconBgVar="--theme-icon-blue"
        iconColorVar="--theme-icon-blue"
      >
        <PreferenceRow
          label="Show Datasource Labels"
          description="Display datasource info when multiple sources"
          checked={showDatasourceLabels}
          onChange={handleDatasourceLabelsChange}
        />
      </PreferenceSection>

      {/* Date & Time Settings */}
      <PreferenceSection
        icon={Calendar}
        title="Date & Time"
        iconBgVar="--theme-icon-green"
        iconColorVar="--theme-icon-green"
      >
        <PreferenceRow
          label="Always Show Year"
          description="Include year in dates even for current year"
          checked={alwaysShowYear}
          onChange={handleAlwaysShowYearChange}
        />
      </PreferenceSection>
    </div>
  );
};

export default DisplayPreferences;
