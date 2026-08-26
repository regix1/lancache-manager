import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Brush, Bell, Database } from 'lucide-react';
import { SettingRow } from '@components/ui/SettingRow';
import { SettingSection } from '@components/ui/SettingSection';
import preferencesService from '@services/preferences.service';
import themeService from '@services/theme.service';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { APP_EVENTS } from '@utils/constants';

const DisplayPreferences: React.FC = () => {
  const { t } = useTranslation();
  const { currentPreferences, setOptimisticPreference } = useSessionPreferences();

  // Visual preferences
  const [sharpCorners, setSharpCorners] = useState(false);
  const [disableTooltips, setDisableTooltips] = useState(false);

  // Notification preferences
  const [disableStickyNotifications, setDisableStickyNotifications] = useState(false);
  const [picsAlwaysVisible, setPicsAlwaysVisible] = useState(false);

  // Downloads preferences
  const [showDatasourceLabels, setShowDatasourceLabels] = useState(true);

  // Initialize from SessionPreferencesContext when preferences are loaded
  useEffect(() => {
    if (currentPreferences) {
      setSharpCorners(currentPreferences.sharpCorners);
      setDisableTooltips(currentPreferences.disableTooltips);
      setDisableStickyNotifications(currentPreferences.disableStickyNotifications);
      setPicsAlwaysVisible(currentPreferences.picsAlwaysVisible);
      setShowDatasourceLabels(currentPreferences.showDatasourceLabels);
    }
  }, [currentPreferences]);

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
      }
    };

    window.addEventListener(APP_EVENTS.PREFERENCE_CHANGED, handlePreferenceChange);
    return () => window.removeEventListener(APP_EVENTS.PREFERENCE_CHANGED, handlePreferenceChange);
  }, []);

  // Handlers for each preference.
  //
  // Each one tells the context the new value as well as its own state. Without that the two
  // disagree for the whole server round-trip, and the mirror effect above reads all five fields
  // while depending on the whole `currentPreferences` object: a broadcast about ANY one preference
  // rewrites the other four from whatever the server last knew. Flipping two toggles quickly was
  // enough to make the second visibly bounce back, and if its save failed it stayed wrong while the
  // user believed it had taken.
  const handleSharpCornersChange = useCallback(
    async (checked: boolean) => {
      setSharpCorners(checked);
      setOptimisticPreference('sharpCorners', checked);
      await themeService.setSharpCorners(checked);
    },
    [setOptimisticPreference]
  );

  const handleTooltipsChange = useCallback(
    async (checked: boolean) => {
      setDisableTooltips(checked);
      setOptimisticPreference('disableTooltips', checked);
      await themeService.setDisableTooltips(checked);
    },
    [setOptimisticPreference]
  );

  const handleStickyNotificationsChange = useCallback(
    async (checked: boolean) => {
      setDisableStickyNotifications(checked);
      setOptimisticPreference('disableStickyNotifications', checked);
      await themeService.setDisableStickyNotifications(checked);
    },
    [setOptimisticPreference]
  );

  const handlePicsVisibleChange = useCallback(
    async (checked: boolean) => {
      setPicsAlwaysVisible(checked);
      setOptimisticPreference('picsAlwaysVisible', checked);
      await themeService.setPicsAlwaysVisible(checked);
    },
    [setOptimisticPreference]
  );

  const handleDatasourceLabelsChange = useCallback(
    async (checked: boolean) => {
      setShowDatasourceLabels(checked);
      setOptimisticPreference('showDatasourceLabels', checked);
      await preferencesService.setPreference('showDatasourceLabels', checked);
    },
    [setOptimisticPreference]
  );

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
      {/* Visual Settings */}
      <SettingSection icon={Brush} title={t('management.sections.displayPreferences.visual')}>
        <SettingRow
          label={t('management.sections.displayPreferences.sharpCorners')}
          description={t('management.sections.displayPreferences.sharpCornersDesc')}
          checked={sharpCorners}
          onChange={handleSharpCornersChange}
        />
        <SettingRow
          label={t('management.sections.displayPreferences.disableTooltips')}
          description={t('management.sections.displayPreferences.disableTooltipsDesc')}
          checked={disableTooltips}
          onChange={handleTooltipsChange}
        />
      </SettingSection>

      {/* Notification Settings */}
      <SettingSection icon={Bell} title={t('management.sections.displayPreferences.notifications')}>
        <SettingRow
          label={t('management.sections.displayPreferences.disableStickyNotifications')}
          description={t('management.sections.displayPreferences.disableStickyNotificationsDesc')}
          checked={disableStickyNotifications}
          onChange={handleStickyNotificationsChange}
        />
        <SettingRow
          label={t('management.sections.displayPreferences.keepNotificationsVisible')}
          description={t('management.sections.displayPreferences.keepNotificationsVisibleDesc')}
          checked={picsAlwaysVisible}
          onChange={handlePicsVisibleChange}
        />
      </SettingSection>

      {/* Downloads Settings */}
      <SettingSection icon={Database} title={t('management.sections.displayPreferences.downloads')}>
        <SettingRow
          label={t('management.sections.displayPreferences.showDatasourceLabels')}
          description={t('management.sections.displayPreferences.showDatasourceLabelsDesc')}
          checked={showDatasourceLabels}
          onChange={handleDatasourceLabelsChange}
        />
      </SettingSection>
    </div>
  );
};

export default DisplayPreferences;
