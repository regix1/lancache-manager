import React, { useEffect, useState, useCallback } from 'react';
import { Users, User } from 'lucide-react';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import { getErrorMessage } from '@utils/error';
import ActiveSessions from './ActiveSessions';
import GuestConfiguration from './GuestConfiguration';
import BulkActions from './BulkActions';
import { Session, ThemeOption, showToast } from './types';

const UserTab: React.FC = () => {
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [guestDurationHours, setGuestDurationHours] = useState<number>(6);
  const [updatingDuration, setUpdatingDuration] = useState(false);
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(false);
  const [updatingGuestLock, setUpdatingGuestLock] = useState(false);
  const [defaultGuestTheme, setDefaultGuestTheme] = useState<string>('dark-default');
  const [updatingGuestTheme, setUpdatingGuestTheme] = useState(false);
  const [availableThemes, setAvailableThemes] = useState<ThemeOption[]>([]);
  const [defaultGuestRefreshRate, setDefaultGuestRefreshRate] = useState<string>('STANDARD');
  const [updatingGuestRefreshRate, setUpdatingGuestRefreshRate] = useState(false);

  const loadGuestDuration = async () => {
    try {
      const response = await fetch('/api/auth/guest/config', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setGuestDurationHours(data.durationHours || 6);
        setGuestModeLocked(data.isLocked || false);
      } else {
        setGuestDurationHours(6);
        setGuestModeLocked(false);
      }
    } catch (err) {
      console.error('Failed to load guest duration:', err);
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
      showToast('error', getErrorMessage(err) || 'Failed to update guest session duration');
    } finally {
      setUpdatingDuration(false);
    }
  };

  const handleToggleGuestLock = async (value?: string) => {
    try {
      setUpdatingGuestLock(true);
      const newLockState = value ? value === 'locked' : !guestModeLocked;
      const response = await fetch('/api/auth/guest/config/lock', {
        method: 'POST',
        headers: {
          ...ApiService.getHeaders(),
          'Content-Type': 'application/json'
        },
        body: JSON.stringify({ isLocked: newLockState })
      });

      if (response.ok) {
        setGuestModeLocked(newLockState);
        showToast(
          'success',
          newLockState
            ? 'Guest mode locked. New guests cannot log in.'
            : 'Guest mode unlocked. Guests can now log in.'
        );
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update guest mode lock');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update guest mode lock');
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
      console.error('Failed to load available themes:', err);
    }
  };

  const loadDefaultGuestTheme = async () => {
    try {
      const response = await fetch('/api/themes/preferences/guest', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestTheme(data.themeId || 'dark-default');
      }
    } catch (err) {
      console.error('Failed to load default guest theme:', err);
    }
  };

  const handleUpdateGuestTheme = async (newThemeId: string) => {
    try {
      setUpdatingGuestTheme(true);
      const response = await fetch('/api/themes/preferences/guest', {
        method: 'PUT',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ themeId: newThemeId })
      });

      if (response.ok) {
        setDefaultGuestTheme(newThemeId);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update default guest theme');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update default guest theme');
    } finally {
      setUpdatingGuestTheme(false);
    }
  };

  const loadDefaultGuestRefreshRate = async () => {
    try {
      const response = await fetch('/api/system/default-guest-refresh-rate', {
        headers: ApiService.getHeaders()
      });
      if (response.ok) {
        const data = await response.json();
        setDefaultGuestRefreshRate(data.refreshRate || 'STANDARD');
      }
    } catch (err) {
      console.error('Failed to load default guest refresh rate:', err);
    }
  };

  const handleUpdateGuestRefreshRate = async (newRate: string) => {
    try {
      setUpdatingGuestRefreshRate(true);
      const response = await fetch('/api/system/default-guest-refresh-rate', {
        method: 'PATCH',
        headers: {
          'Content-Type': 'application/json',
          ...ApiService.getHeaders()
        },
        body: JSON.stringify({ refreshRate: newRate })
      });

      if (response.ok) {
        setDefaultGuestRefreshRate(newRate);
        showToast('success', 'Default guest refresh rate updated');
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to update default guest refresh rate');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to update default guest refresh rate');
    } finally {
      setUpdatingGuestRefreshRate(false);
    }
  };

  const handleSessionsChange = useCallback(() => {
    // This is called when sessions need to be refreshed from child components
  }, []);

  useEffect(() => {
    loadGuestDuration();
    loadAvailableThemes();
    loadDefaultGuestTheme();
    loadDefaultGuestRefreshRate();
  }, []);

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-5 sm:space-y-6">
      {/* Header with integrated stats */}
      <div className="flex flex-col sm:flex-row sm:items-center sm:justify-between gap-4">
        <div className="flex items-center gap-3">
          <div
            className="w-12 h-12 rounded-xl flex items-center justify-center flex-shrink-0"
            style={{
              backgroundColor: 'var(--theme-primary-subtle)',
              boxShadow: '0 4px 12px rgba(0, 0, 0, 0.1)'
            }}
          >
            <Users className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
          </div>
          <div>
            <h1
              className="text-xl sm:text-2xl font-bold tracking-tight"
              style={{ color: 'var(--theme-text-primary)' }}
            >
              User Management
            </h1>
            <p className="text-sm" style={{ color: 'var(--theme-text-muted)' }}>
              Sessions & guest controls
            </p>
          </div>
        </div>

        {/* Stats pills + refresh */}
        <div className="flex items-center gap-2 flex-wrap">
          <div className="stat-pill">
            <Users className="w-4 h-4" style={{ color: 'var(--theme-primary)' }} />
            <span className="stat-value" style={{ color: 'var(--theme-text-primary)' }}>
              {sessions.length}
            </span>
            <span style={{ color: 'var(--theme-text-muted)' }}>total</span>
          </div>
          <div className="stat-pill">
            <User className="w-4 h-4" style={{ color: 'var(--theme-user-session)' }} />
            <span className="stat-value" style={{ color: 'var(--theme-text-primary)' }}>
              {sessions.filter((s) => s.type === 'authenticated').length}
            </span>
            <span style={{ color: 'var(--theme-text-muted)' }}>users</span>
          </div>
          <div className="stat-pill">
            <User className="w-4 h-4" style={{ color: 'var(--theme-guest-session)' }} />
            <span className="stat-value" style={{ color: 'var(--theme-text-primary)' }}>
              {sessions.filter((s) => s.type === 'guest').length}
            </span>
            <span style={{ color: 'var(--theme-text-muted)' }}>guests</span>
          </div>

        </div>
      </div>

      {/* Active Sessions */}
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
      />

      {/* Guest Configuration */}
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
        availableThemes={availableThemes}
      />

      {/* Bulk Actions */}
      <BulkActions onSessionsChange={handleSessionsChange} />
    </div>
  );
};

export default UserTab;
