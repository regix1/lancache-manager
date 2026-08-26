import React, { useState, useEffect, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Brush, Bell, Database } from 'lucide-react';
import { SettingRow } from '@components/ui/SettingRow';
import { SettingSection } from '@components/ui/SettingSection';
import preferencesService from '@services/preferences.service';
import themeService from '@services/theme.service';
import { useSessionPreferences } from '@contexts/useSessionPreferences';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { APP_EVENTS } from '@utils/constants';
import { dropPendingPreference, setPendingPreference } from '@utils/pendingPreferences';
import type { OptimisticToggleKey } from '@/types/userPreferences';

/** One switch's write: what it is called, what it shows now, and how to put a new value up. */
interface DisplayPreferenceWrite {
  key: OptimisticToggleKey;
  previous: boolean;
  setLocal: (value: boolean) => void;
  save: (value: boolean) => Promise<boolean>;
}

const DisplayPreferences: React.FC = () => {
  const { t } = useTranslation();
  const { currentPreferences, setOptimisticPreference } = useSessionPreferences();
  const { notifyError } = useErrorHandler();

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
  // enough to make the second visibly bounce back.
  //
  // The picked value is also held in the pending store for the length of the save, because the
  // context's own state is not what a broadcast overwrites - the broadcast rebuilds the whole row
  // from the server, and only a key the store is holding survives that. It is released either way
  // once the save answers, so a real server change is never ignored for longer than one round trip.
  const writePreference = useCallback(
    async (write: DisplayPreferenceWrite, checked: boolean) => {
      write.setLocal(checked);
      setOptimisticPreference(write.key, checked);
      setPendingPreference(write.key, checked);

      try {
        if (await write.save(checked)) return;

        // The save was refused, so the switch is showing a preference the server never took.
        write.setLocal(write.previous);
        setOptimisticPreference(write.key, write.previous);
        notifyError(t('management.sections.displayPreferences.saveFailed'));
      } finally {
        dropPendingPreference(write.key);
      }
    },
    [setOptimisticPreference, notifyError, t]
  );

  const handleSharpCornersChange = useCallback(
    (checked: boolean) =>
      writePreference(
        {
          key: 'sharpCorners',
          previous: sharpCorners,
          setLocal: setSharpCorners,
          save: (value) => themeService.setSharpCorners(value)
        },
        checked
      ),
    [writePreference, sharpCorners]
  );

  const handleTooltipsChange = useCallback(
    (checked: boolean) =>
      writePreference(
        {
          key: 'disableTooltips',
          previous: disableTooltips,
          setLocal: setDisableTooltips,
          save: (value) => themeService.setDisableTooltips(value)
        },
        checked
      ),
    [writePreference, disableTooltips]
  );

  const handleStickyNotificationsChange = useCallback(
    (checked: boolean) =>
      writePreference(
        {
          key: 'disableStickyNotifications',
          previous: disableStickyNotifications,
          setLocal: setDisableStickyNotifications,
          save: (value) => themeService.setDisableStickyNotifications(value)
        },
        checked
      ),
    [writePreference, disableStickyNotifications]
  );

  const handlePicsVisibleChange = useCallback(
    (checked: boolean) =>
      writePreference(
        {
          key: 'picsAlwaysVisible',
          previous: picsAlwaysVisible,
          setLocal: setPicsAlwaysVisible,
          save: (value) => themeService.setPicsAlwaysVisible(value)
        },
        checked
      ),
    [writePreference, picsAlwaysVisible]
  );

  const handleDatasourceLabelsChange = useCallback(
    (checked: boolean) =>
      writePreference(
        {
          key: 'showDatasourceLabels',
          previous: showDatasourceLabels,
          setLocal: setShowDatasourceLabels,
          save: (value) => preferencesService.setPreference('showDatasourceLabels', value)
        },
        checked
      ),
    [writePreference, showDatasourceLabels]
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
