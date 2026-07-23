import React, { useEffect, useState, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import { Users, Settings2 } from 'lucide-react';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { AccordionGroupProvider } from '@components/ui/AccordionGroupProvider';
import { AccordionGroupToggle } from '@components/ui/AccordionGroupToggle';
import ActiveSessions from './ActiveSessions';
import GuestConfiguration from './GuestConfiguration';
import { type Session, type SessionFilter, type ThemeOption, showToast } from './types';

const UserTab: React.FC = () => {
  const { t } = useTranslation();
  const { notifyError } = useErrorHandler();
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
  const [activeFilter, setActiveFilter] = useState<SessionFilter>('all');

  const loadGuestDuration = async () => {
    try {
      const data = await ApiService.getGuestConfig<{ durationHours: number; isLocked: boolean }>();
      setGuestDurationHours(data.durationHours || 6);
      setGuestModeLocked(data.isLocked);
    } catch (err) {
      notifyError(t('user.errors.loadGuestDuration'), err, {
        logLabel: 'Failed to load guest duration'
      });
      setGuestDurationHours(6);
      setGuestModeLocked(false);
    }
  };

  const handleUpdateDuration = async (newDuration: number) => {
    try {
      setUpdatingDuration(true);
      const response = await ApiService.setGuestSessionDuration(newDuration);
      setGuestDurationHours(response.durationHours);
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestDuration'), err, {
        logLabel: 'Failed to update guest duration'
      });
    } finally {
      setUpdatingDuration(false);
    }
  };

  const handleToggleGuestLock = async (value?: string) => {
    try {
      setUpdatingGuestLock(true);
      const newLockState = value ? value === 'locked' : !guestModeLocked;
      await ApiService.setGuestConfigLock(newLockState);
      setGuestModeLocked(newLockState);
      showToast('success', newLockState ? t('user.locked') : t('user.unlocked'));
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestLock'), err, {
        logLabel: 'Failed to update guest lock'
      });
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
      notifyError(t('user.errors.loadThemes'), err, { logLabel: 'Failed to load themes' });
    }
  };

  const loadDefaultGuestTheme = async () => {
    try {
      const data = await ApiService.getGuestThemePreference<{ themeId: string }>();
      setDefaultGuestTheme(data.themeId || 'dark-default');
    } catch (err) {
      notifyError(t('user.errors.loadGuestTheme'), err, {
        logLabel: 'Failed to load guest theme'
      });
    }
  };

  const handleUpdateGuestTheme = async (newThemeId: string) => {
    try {
      setUpdatingGuestTheme(true);
      await ApiService.setGuestThemePreference(newThemeId);
      setDefaultGuestTheme(newThemeId);
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestTheme'), err, {
        logLabel: 'Failed to update guest theme'
      });
    } finally {
      setUpdatingGuestTheme(false);
    }
  };

  const loadDefaultGuestRefreshRate = async () => {
    try {
      const data = await ApiService.getDefaultGuestRefreshRate<{
        refreshRate: string;
        locked: boolean;
      }>();
      setDefaultGuestRefreshRate(data.refreshRate || 'STANDARD');
      setGuestRefreshRateLocked(data.locked);
    } catch (err) {
      notifyError(t('user.errors.loadGuestRefreshRate'), err, {
        logLabel: 'Failed to load guest refresh rate'
      });
    }
  };

  const handleUpdateGuestRefreshRate = async (newRate: string) => {
    try {
      setUpdatingGuestRefreshRate(true);
      await ApiService.setDefaultGuestRefreshRate(newRate);
      setDefaultGuestRefreshRate(newRate);
      showToast('success', t('user.refreshRateUpdated'));
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestRefreshRate'), err, {
        logLabel: 'Failed to update guest refresh rate'
      });
    } finally {
      setUpdatingGuestRefreshRate(false);
    }
  };

  const handleUpdateGuestRefreshRateLock = async (locked: boolean) => {
    try {
      setUpdatingGuestRefreshRateLock(true);
      await ApiService.setGuestRefreshRateLock(locked);
      setGuestRefreshRateLocked(locked);
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestRefreshRateLock'), err, {
        logLabel: 'Failed to update guest refresh rate lock'
      });
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
        </div>
      </div>

      {/* Tab Bar */}
      <SegmentedControl
        options={[
          { value: 'sessions', label: t('user.tabs.sessions', 'Sessions'), icon: <Users /> },
          {
            value: 'defaults',
            label: t('user.tabs.guestDefaults', 'Guest Defaults'),
            icon: <Settings2 />
          }
        ]}
        value={activeTab}
        onChange={(value: string) => setActiveTab(value as 'sessions' | 'defaults')}
        size="md"
        showLabels="responsive"
      />

      {/* Tab Content - keyed by activeTab so AccordionGroupProvider's registry always
          starts empty for the newly active tab. */}
      <AccordionGroupProvider key={activeTab}>
        <div className="flex justify-end">
          <AccordionGroupToggle />
        </div>

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
      </AccordionGroupProvider>
    </div>
  );
};

export default UserTab;
