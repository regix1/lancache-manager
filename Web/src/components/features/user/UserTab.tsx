import React, { useEffect, useState, useCallback, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { Users, Settings2, UserCog } from 'lucide-react';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { getErrorMessage } from '@utils/error';
import { useErrorHandler } from '@hooks/useErrorHandler';
import { useReconnectRefetch } from '@hooks/useReconnectRefetch';
import { useSignalR } from '@contexts/SignalRContext/useSignalR';
import { SegmentedControl } from '@components/ui/SegmentedControl';
import { AccordionGroupProvider } from '@components/ui/AccordionGroupProvider';
import { ErrorBlock } from '@components/ui/ErrorBlock';
import { LoadingState } from '@components/ui/ManagerCard';
import ActiveSessions from './ActiveSessions';
import GuestConfiguration from './GuestConfiguration';
import SignInMethodCard from './SignInMethodCard';
import UserAccounts from './UserAccounts';
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

  const [guestDefaultsError, setGuestDefaultsError] = useState<string | null>(null);
  const [guestDefaultsLoaded, setGuestDefaultsLoaded] = useState(false);

  const [activeTab, setActiveTab] = useState<'sessions' | 'accounts' | 'defaults'>('sessions');
  const [activeFilter, setActiveFilter] = useState<SessionFilter>('all');

  const guestDefaultsRequestRef = useRef(0);
  // Pushes received per setting. The server announces a save before it answers it, so a save's reply
  // applies only when no push for its setting arrived while it was open.
  const guestDefaultPushesRef = useRef({
    duration: 0,
    guestLock: 0,
    theme: 0,
    refreshRate: 0,
    refreshRateLock: 0
  });

  const loadGuestDefaults = useCallback(async () => {
    // Mount, reconnect and Retry can overlap; only the newest request writes values or the error.
    const request = ++guestDefaultsRequestRef.current;
    // A push received while the reads are out is newer than what they read.
    const pushesBefore = { ...guestDefaultPushesRef.current };
    setGuestDefaultsError(null);
    try {
      const [guestConfig, guestTheme, guestRefreshRate, themeResult] = await Promise.all([
        ApiService.getGuestConfig<{ durationHours: number; isLocked: boolean }>(),
        ApiService.getGuestThemePreference<{ themeId: string }>(),
        ApiService.getDefaultGuestRefreshRate<{ refreshRate: string; locked: boolean }>(),
        themeService.loadThemes()
      ]);
      if (request !== guestDefaultsRequestRef.current) return;
      const pushes = guestDefaultPushesRef.current;
      if (pushes.duration === pushesBefore.duration)
        setGuestDurationHours(guestConfig.durationHours || 6);
      if (pushes.guestLock === pushesBefore.guestLock) setGuestModeLocked(guestConfig.isLocked);
      if (pushes.theme === pushesBefore.theme)
        setDefaultGuestTheme(guestTheme.themeId || 'dark-default');
      if (pushes.refreshRate === pushesBefore.refreshRate)
        setDefaultGuestRefreshRate(guestRefreshRate.refreshRate || 'STANDARD');
      if (pushes.refreshRateLock === pushesBefore.refreshRateLock)
        setGuestRefreshRateLocked(guestRefreshRate.locked);
      setAvailableThemes(
        themeResult.themes.map((theme) => ({ id: theme.meta.id, name: theme.meta.name }))
      );
      setGuestDefaultsError(themeResult.loadError);
      setGuestDefaultsLoaded(true);
    } catch (err) {
      if (request !== guestDefaultsRequestRef.current) return;
      setGuestDefaultsError(getErrorMessage(err));
    }
  }, []);

  const handleUpdateDuration = async (newDuration: number) => {
    try {
      setUpdatingDuration(true);
      const pushesBefore = guestDefaultPushesRef.current.duration;
      const response = await ApiService.setGuestSessionDuration(newDuration);
      if (guestDefaultPushesRef.current.duration === pushesBefore)
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
      const pushesBefore = guestDefaultPushesRef.current.guestLock;
      await ApiService.setGuestConfigLock(newLockState);
      if (guestDefaultPushesRef.current.guestLock === pushesBefore)
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

  const handleUpdateGuestTheme = async (newThemeId: string) => {
    try {
      setUpdatingGuestTheme(true);
      const pushesBefore = guestDefaultPushesRef.current.theme;
      await ApiService.setGuestThemePreference(newThemeId);
      if (guestDefaultPushesRef.current.theme === pushesBefore) setDefaultGuestTheme(newThemeId);
    } catch (err: unknown) {
      notifyError(t('user.errors.updateGuestTheme'), err, {
        logLabel: 'Failed to update guest theme'
      });
    } finally {
      setUpdatingGuestTheme(false);
    }
  };

  const handleUpdateGuestRefreshRate = async (newRate: string) => {
    try {
      setUpdatingGuestRefreshRate(true);
      const pushesBefore = guestDefaultPushesRef.current.refreshRate;
      await ApiService.setDefaultGuestRefreshRate(newRate);
      if (guestDefaultPushesRef.current.refreshRate === pushesBefore)
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
      const pushesBefore = guestDefaultPushesRef.current.refreshRateLock;
      await ApiService.setGuestRefreshRateLock(locked);
      if (guestDefaultPushesRef.current.refreshRateLock === pushesBefore)
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
  const { on, off, isConnected } = useSignalR();

  const handleGuestModeLockChanged = useCallback((data: { isLocked: boolean }) => {
    guestDefaultPushesRef.current.guestLock += 1;
    setGuestModeLocked(data.isLocked);
  }, []);

  const handleGuestDurationUpdated = useCallback((data: { durationHours: number }) => {
    guestDefaultPushesRef.current.duration += 1;
    setGuestDurationHours(data.durationHours);
  }, []);

  const handleDefaultGuestThemeChanged = useCallback((data: { newThemeId: string }) => {
    guestDefaultPushesRef.current.theme += 1;
    setDefaultGuestTheme(data.newThemeId);
  }, []);

  const handleDefaultGuestRefreshRateChanged = useCallback((data: { refreshRate: string }) => {
    guestDefaultPushesRef.current.refreshRate += 1;
    setDefaultGuestRefreshRate(data.refreshRate);
  }, []);

  const handleGuestRefreshRateLockChanged = useCallback((data: { locked: boolean }) => {
    guestDefaultPushesRef.current.refreshRateLock += 1;
    setGuestRefreshRateLocked(data.locked);
  }, []);

  // Refresh guest defaults when SignalR reconnects (catches config events missed during disconnect)
  useReconnectRefetch(isConnected, () => void loadGuestDefaults());

  useEffect(() => {
    void loadGuestDefaults();

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
      {/* Tab Bar */}
      <SegmentedControl
        options={[
          { value: 'sessions', label: t('user.tabs.sessions'), icon: <Users /> },
          { value: 'accounts', label: t('user.tabs.accounts'), icon: <UserCog /> },
          {
            value: 'defaults',
            label: t('user.tabs.guestDefaults'),
            icon: <Settings2 />
          }
        ]}
        value={activeTab}
        onChange={(value: string) => setActiveTab(value as 'sessions' | 'accounts' | 'defaults')}
        size="md"
        showLabels="responsive"
        fullWidth
      />

      {/* The Sessions tab shows values from these reads too, so the box sits above both tabs */}
      {guestDefaultsError !== null && activeTab !== 'accounts' && (
        <ErrorBlock
          title={t('user.guest.errors.loadDefaults')}
          message={guestDefaultsError}
          retryLabel={t('common.retry')}
          onRetry={() => void loadGuestDefaults()}
        />
      )}

      {/* Tab Content - keyed by activeTab so AccordionGroupProvider's registry always
          starts empty for the newly active tab. */}
      <AccordionGroupProvider key={activeTab}>
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
              guestDefaultsLoaded={guestDefaultsLoaded}
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

        {activeTab === 'accounts' && (
          <div className="user-tab-content">
            <div className="space-y-4">
              <SignInMethodCard />
              <UserAccounts />
            </div>
          </div>
        )}

        {activeTab === 'defaults' && !guestDefaultsLoaded && guestDefaultsError === null && (
          <LoadingState />
        )}

        {activeTab === 'defaults' && guestDefaultsLoaded && (
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
