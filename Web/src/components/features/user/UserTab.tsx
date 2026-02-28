import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Users, Settings2 } from 'lucide-react';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { getErrorMessage } from '@utils/error';
import { useSignalR } from '@contexts/SignalRContext';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import ActiveSessions from './ActiveSessions';
import GuestConfiguration from './GuestConfiguration';
import { type Session, type ThemeOption, showToast } from './types';

const UserTab: React.FC = () => {
  const { t } = useTranslation();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [sessionRefreshKey, setSessionRefreshKey] = useState(0);
  const [guestDurationHours, setGuestDurationHours] = useState<number>(6);
  const [updatingDuration, setUpdatingDuration] = useState(false);
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(false);
  const [updatingGuestLock, setUpdatingGuestLock] = useState(false);
  const [defaultGuestTheme, setDefaultGuestTheme] = useState<string>('dark-default');
  const [updatingGuestTheme, setUpdatingGuestTheme] = useState(false);
  const [availableThemes, setAvailableThemes] = useState<ThemeOption[]>([]);
  const [defaultGuestRefreshRate, setDefaultGuestRefreshRate] = useState<string>('STANDARD');
  const [updatingGuestRefreshRate, setUpdatingGuestRefreshRate] = useState(false);
  const [guestRefreshRateLocked, setGuestRefreshRateLocked] = useState<boolean>(true);
  const [updatingGuestRefreshRateLock, setUpdatingGuestRefreshRateLock] = useState(false);

  const [activeTab, setActiveTab] = useState<'sessions' | 'defaults'>('sessions');
  const [activeFilter, setActiveFilter] = useState<'all' | 'admin' | 'guest'>('all');

  const loadGuestDuration = async () => {
    try {
      const response = await fetch('/api/auth/guest/config', ApiService.getFetchOptions());
      if (response.ok) {
        const data = await response.json();
        setGuestDurationHours(data.durationHours || 6);
        setGuestModeLocked(data.isLocked || false);
      } else {
        setGuestDurationHours(6);
        setGuestModeLocked(false);
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.errors.loadGuestDuration'));
      setGuestDurationHours(6);
      setGuestModeLocked(false);
    }
  };

  const handleUpdateDuration = async (newDuration: number) => {
    try {
      setUpdatingDuration(true);
      await ApiService.setGuestSessionDuration(newDuration);
      setGuestDurationHours(newDuration);
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.errors.updateGuestDuration'));
    } finally {
      setUpdatingDuration(false);
    }
  };

  const handleToggleGuestLock = async (value?: string) => {
    try {
      setUpdatingGuestLock(true);
      const newLockState = value ? value === 'locked' : !guestModeLocked;
      const response = await fetch(
        '/api/auth/guest/config/lock',
        ApiService.getFetchOptions({
          method: 'POST',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ isLocked: newLockState })
        })
      );

      if (response.ok) {
        setGuestModeLocked(newLockState);
        showToast('success', newLockState ? t('user.locked') : t('user.unlocked'));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.errors.updateGuestLock'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.errors.updateGuestLock'));
    } finally {
      setUpdatingGuestLock(false);
    }
  };

  const loadAvailableThemes = async () => {
    try {
      const themes = await themeService.loadThemes();
      setAvailableThemes(
        themes.map((theme) => ({
          id: theme.meta.id,
          name: theme.meta.name
        }))
      );
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.errors.loadThemes'));
    }
  };

  const loadDefaultGuestTheme = async () => {
    try {
      const response = await fetch('/api/themes/preferences/guest', ApiService.getFetchOptions());
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestTheme(data.themeId || 'dark-default');
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.errors.loadGuestTheme'));
    }
  };

  const handleUpdateGuestTheme = async (newThemeId: string) => {
    try {
      setUpdatingGuestTheme(true);
      const response = await fetch(
        '/api/themes/preferences/guest',
        ApiService.getFetchOptions({
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ themeId: newThemeId })
        })
      );

      if (response.ok) {
        setDefaultGuestTheme(newThemeId);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.errors.updateGuestTheme'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.errors.updateGuestTheme'));
    } finally {
      setUpdatingGuestTheme(false);
    }
  };

  const loadDefaultGuestRefreshRate = async () => {
    try {
      const response = await fetch(
        '/api/system/default-guest-refresh-rate',
        ApiService.getFetchOptions()
      );
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestRefreshRate(data.refreshRate || 'STANDARD');
        setGuestRefreshRateLocked(data.locked ?? true);
      }
    } catch (err) {
      showToast('error', getErrorMessage(err) || t('user.errors.loadGuestRefreshRate'));
    }
  };

  const handleUpdateGuestRefreshRate = async (newRate: string) => {
    try {
      setUpdatingGuestRefreshRate(true);
      const response = await fetch(
        '/api/system/default-guest-refresh-rate',
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ refreshRate: newRate })
        })
      );

      if (response.ok) {
        setDefaultGuestRefreshRate(newRate);
        showToast('success', t('user.refreshRateUpdated'));
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || t('user.errors.updateGuestRefreshRate'));
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || t('user.errors.updateGuestRefreshRate'));
    } finally {
      setUpdatingGuestRefreshRate(false);
    }
  };

  const handleUpdateGuestRefreshRateLock = async (locked: boolean) => {
    try {
      setUpdatingGuestRefreshRateLock(true);
      const response = await fetch(
        '/api/system/guest-refresh-rate-lock',
        ApiService.getFetchOptions({
          method: 'PATCH',
          headers: {
            'Content-Type': 'application/json'
          },
          body: JSON.stringify({ locked })
        })
      );

      if (response.ok) {
        setGuestRefreshRateLocked(locked);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update refresh rate lock');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update refresh rate lock');
    } finally {
      setUpdatingGuestRefreshRateLock(false);
    }
  };

  const handleSessionsChange = useCallback(() => {
    setSessionRefreshKey((prev: number) => prev + 1);
  }, []);

  // SignalR handlers for live config updates
  const { on, off } = useSignalR();

  const handleGuestModeLockChanged = useCallback((data: { isLocked: boolean }) => {
    setGuestModeLocked(data.isLocked);
  }, []);

  const handleGuestDurationUpdated = useCallback((data: { durationHours: number }) => {
    setGuestDurationHours(data.durationHours);
  }, []);

  const handleDefaultGuestThemeChanged = useCallback((data: { newThemeId: string }) => {
    setDefaultGuestTheme(data.newThemeId);
  }, []);

  const handleDefaultGuestRefreshRateChanged = useCallback((data: { refreshRate: string }) => {
    setDefaultGuestRefreshRate(data.refreshRate);
  }, []);

  const handleGuestRefreshRateLockChanged = useCallback((data: { locked: boolean }) => {
    setGuestRefreshRateLocked(data.locked);
  }, []);

  useEffect(() => {
    loadGuestDuration();
    loadAvailableThemes();
    loadDefaultGuestTheme();
    loadDefaultGuestRefreshRate();

    on('GuestModeLockChanged', handleGuestModeLockChanged);
    on('GuestDurationUpdated', handleGuestDurationUpdated);
    on('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
    on('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
    on('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);

    return () => {
      off('GuestModeLockChanged', handleGuestModeLockChanged);
      off('GuestDurationUpdated', handleGuestDurationUpdated);
      off('DefaultGuestThemeChanged', handleDefaultGuestThemeChanged);
      off('DefaultGuestRefreshRateChanged', handleDefaultGuestRefreshRateChanged);
      off('GuestRefreshRateLockChanged', handleGuestRefreshRateLockChanged);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    on,
    off,
    handleGuestModeLockChanged,
    handleGuestDurationUpdated,
    handleDefaultGuestThemeChanged,
    handleDefaultGuestRefreshRateChanged,
    handleGuestRefreshRateLockChanged
  ]);

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-5 sm:space-y-6 animate-fadeIn">
      {/* Header */}
      <div className="flex items-center gap-3">
        <div className="w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0 bg-themed-accent-subtle shadow-md">
          <Users className="w-6 h-6 text-themed-accent" />
        </div>
        <div>
          <h1 className="text-xl sm:text-2xl font-bold tracking-tight text-themed-primary">
            {t('user.title')}
          </h1>
          <p className="text-sm text-themed-muted">{t('user.subtitle')}</p>
        </div>
      </div>

      {/* Tab Bar */}
      <SegmentedControl
        options={[
          { value: 'sessions', label: 'Sessions', icon: <Users /> },
          { value: 'defaults', label: 'Guest Defaults', icon: <Settings2 /> }
        ]}
        value={activeTab}
        onChange={(value: string) => setActiveTab(value as 'sessions' | 'defaults')}
        size="md"
        showLabels="responsive"
      />

      {/* Tab Content */}
      {activeTab === 'sessions' && (
        <div className="user-tab-content">
          <ActiveSessions
            guestDurationHours={guestDurationHours}
            guestModeLocked={guestModeLocked}
            updatingGuestLock={updatingGuestLock}
            onToggleGuestLock={handleToggleGuestLock}
            availableThemes={availableThemes}
            defaultGuestTheme={defaultGuestTheme}
            defaultGuestRefreshRate={defaultGuestRefreshRate}
            sessions={sessions}
            setSessions={setSessions}
            loading={loading}
            setLoading={setLoading}
            onSessionsChange={handleSessionsChange}
            refreshKey={sessionRefreshKey}
            activeFilter={activeFilter}
            onFilterChange={setActiveFilter}
          />
        </div>
      )}

      {activeTab === 'defaults' && (
        <div className="user-tab-content">
          <GuestConfiguration
            guestDurationHours={guestDurationHours}
            onDurationChange={handleUpdateDuration}
            updatingDuration={updatingDuration}
            defaultGuestTheme={defaultGuestTheme}
            onGuestThemeChange={handleUpdateGuestTheme}
            updatingGuestTheme={updatingGuestTheme}
            defaultGuestRefreshRate={defaultGuestRefreshRate}
            onGuestRefreshRateChange={handleUpdateGuestRefreshRate}
            updatingGuestRefreshRate={updatingGuestRefreshRate}
            guestRefreshRateLocked={guestRefreshRateLocked}
            onGuestRefreshRateLockChange={handleUpdateGuestRefreshRateLock}
            updatingGuestRefreshRateLock={updatingGuestRefreshRateLock}
            availableThemes={availableThemes}
          />
        </div>
      )}
    </div>
  );
};

export default UserTab;
