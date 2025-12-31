import React, { useEffect, useState, useCallback } from 'react';
import {
  Users,
  User,
  Trash2,
  RefreshCw,
  Loader2,
  AlertTriangle,
  Clock,
  Network,
  Monitor,
  Globe,
  Edit,
  Lock,
  Unlock,
  ChevronDown
} from 'lucide-react';
import { Button } from '@components/ui/Button';
import { Card } from '@components/ui/Card';
import { Modal } from '@components/ui/Modal';
import { Alert } from '@components/ui/Alert';
import { HelpPopover } from '@components/ui/HelpPopover';
import { EnhancedDropdown } from '@components/ui/EnhancedDropdown';
import { Pagination } from '@components/ui/Pagination';
import { ToggleSwitch } from '@components/ui/ToggleSwitch';
import { ClientIpDisplay } from '@components/ui/ClientIpDisplay';
import ApiService from '@services/api.service';
import themeService from '@services/theme.service';
import authService from '@services/auth.service';
import { getErrorMessage } from '@utils/error';
import { useAuth } from '@contexts/AuthContext';
import { useFormattedDateTime } from '@hooks/useFormattedDateTime';
import { useSignalR } from '@contexts/SignalRContext';
import { useActivityTracker } from '@hooks/useActivityTracker';

interface Session {
  id: string;
  deviceId?: string | null;
  deviceName: string | null;
  ipAddress: string | null;
  localIp: string | null;
  hostname: string | null;
  operatingSystem: string | null;
  browser: string | null;
  createdAt: string;
  lastSeenAt: string | null;
  expiresAt: string;
  isExpired: boolean;
  isRevoked: boolean;
  revokedAt?: string | null;
  revokedBy?: string | null;
  type: 'authenticated' | 'guest';
}

interface UserPreferences {
  selectedTheme: string | null;
  sharpCorners: boolean;
  disableFocusOutlines: boolean;
  disableTooltips: boolean;
  picsAlwaysVisible: boolean;
  disableStickyNotifications: boolean;
  showDatasourceLabels: boolean;
  refreshRate?: string | null; // Refresh rate for guest users
}

// Helper to format timestamp with timezone awareness
const FormattedTimestamp: React.FC<{ timestamp: string }> = ({ timestamp }) => {
  const formattedTime = useFormattedDateTime(timestamp);
  return <>{formattedTime}</>;
};

// Helper to clean IP addresses
const cleanIpAddress = (ip: string): string => {
  const cleanIp = ip.replace('::ffff:', '');
  if (cleanIp === '::1' || cleanIp === '127.0.0.1') {
    return 'localhost';
  }
  return cleanIp;
};

const UserTab: React.FC = () => {
  const { refreshAuth } = useAuth();
  const { on, off } = useSignalR();
  const [sessions, setSessions] = useState<Session[]>([]);
  const [loading, setLoading] = useState(true);
  const [revokingSession, setRevokingSession] = useState<string | null>(null);
  const [expandedSessions, setExpandedSessions] = useState<Set<string>>(new Set());

  const { isActive: isLocallyActive } = useActivityTracker();
  const currentDeviceId = authService.getDeviceId();

  const showToast = (type: 'success' | 'error' | 'info', message: string) => {
    window.dispatchEvent(new CustomEvent('show-toast', {
      detail: { type, message, duration: 4000 }
    }));
  };

  const [deletingSession, setDeletingSession] = useState<string | null>(null);
  const [pendingRevokeSession, setPendingRevokeSession] = useState<Session | null>(null);
  const [pendingDeleteSession, setPendingDeleteSession] = useState<Session | null>(null);
  const [guestDurationHours, setGuestDurationHours] = useState<number>(6);
  const [updatingDuration, setUpdatingDuration] = useState(false);
  const [guestModeLocked, setGuestModeLocked] = useState<boolean>(false);
  const [updatingGuestLock, setUpdatingGuestLock] = useState(false);
  const [defaultGuestTheme, setDefaultGuestTheme] = useState<string>('dark-default');
  const [updatingGuestTheme, setUpdatingGuestTheme] = useState(false);
  const [availableThemes, setAvailableThemes] = useState<{ id: string; name: string }[]>([]);
  const [defaultGuestRefreshRate, setDefaultGuestRefreshRate] = useState<string>('STANDARD');
  const [updatingGuestRefreshRate, setUpdatingGuestRefreshRate] = useState(false);
  const [editingSession, setEditingSession] = useState<Session | null>(null);
  const [editingPreferences, setEditingPreferences] = useState<UserPreferences | null>(null);
  const [loadingPreferences, setLoadingPreferences] = useState(false);
  const [savingPreferences, setSavingPreferences] = useState(false);
  const [currentPage, setCurrentPage] = useState(1);
  const [pageSize] = useState(20);
  const [totalPages, setTotalPages] = useState(1);
  const [totalCount, setTotalCount] = useState(0);

  const toggleSessionExpanded = (sessionId: string) => {
    setExpandedSessions(prev => {
      const next = new Set(prev);
      if (next.has(sessionId)) {
        next.delete(sessionId);
      } else {
        next.add(sessionId);
      }
      return next;
    });
  };

  const loadSessions = useCallback(async (showLoading = false, page = currentPage) => {
    try {
      if (showLoading) {
        setLoading(true);
      }
      const response = await fetch(`/api/sessions?page=${page}&pageSize=${pageSize}`, {
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        const data = await response.json();
        const sessions = data.sessions || [];
        const deviceIds = sessions.map((s: Session) => s.deviceId || s.id);
        const uniqueDeviceIds = new Set(deviceIds);
        if (deviceIds.length !== uniqueDeviceIds.size) {
          console.warn('[UserTab] Duplicate sessions detected in API response:', sessions);
        }

        setSessions(sessions);
        setTotalPages(data.pagination?.totalPages || 1);
        setTotalCount(data.pagination?.totalCount || sessions.length);
        setCurrentPage(data.pagination?.page || 1);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to load sessions');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to load sessions');
    } finally {
      if (showLoading) {
        setLoading(false);
      }
    }
  }, [currentPage, pageSize]);

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

  const refreshRateOptions = [
    { value: 'LIVE', label: 'Live (Real-time)' },
    { value: 'ULTRA', label: 'Ultra (1s)' },
    { value: 'REALTIME', label: 'Real-time (5s)' },
    { value: 'STANDARD', label: 'Standard (10s)' },
    { value: 'RELAXED', label: 'Relaxed (30s)' },
    { value: 'SLOW', label: 'Slow (60s)' }
  ];

  const durationOptions = [
    { value: '1', label: '1 hour' },
    { value: '2', label: '2 hours' },
    { value: '3', label: '3 hours' },
    { value: '6', label: '6 hours' },
    { value: '12', label: '12 hours' },
    { value: '24', label: '24 hours (1 day)' },
    { value: '48', label: '48 hours (2 days)' },
    { value: '72', label: '72 hours (3 days)' },
    { value: '168', label: '168 hours (1 week)' }
  ];

  const handleSessionRevoked = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionsCleared = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionCreated = useCallback(() => {
    loadSessions(false);
  }, [loadSessions]);

  const handleSessionLastSeenUpdated = useCallback((data: { deviceId: string; lastSeenAt: string }) => {
    setSessions(prev => prev.map(session => {
      if (session.id === data.deviceId || session.deviceId === data.deviceId) {
        return { ...session, lastSeenAt: data.lastSeenAt };
      }
      return session;
    }));
  }, []);

  useEffect(() => {
    loadSessions(true);
    loadGuestDuration();
    loadAvailableThemes();
    loadDefaultGuestTheme();
    loadDefaultGuestRefreshRate();

    on('UserSessionRevoked', handleSessionRevoked);
    on('UserSessionsCleared', handleSessionsCleared);
    on('UserSessionCreated', handleSessionCreated);
    on('SessionLastSeenUpdated', handleSessionLastSeenUpdated);

    const pollInterval = setInterval(() => {
      loadSessions(false);
    }, 30000);

    const handleVisibilityChange = () => {
      if (!document.hidden) {
        loadSessions(false);
      }
    };
    document.addEventListener('visibilitychange', handleVisibilityChange);

    return () => {
      off('UserSessionRevoked', handleSessionRevoked);
      off('UserSessionsCleared', handleSessionsCleared);
      off('UserSessionCreated', handleSessionCreated);
      off('SessionLastSeenUpdated', handleSessionLastSeenUpdated);
      clearInterval(pollInterval);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
    };
  }, [loadSessions, on, off, handleSessionRevoked, handleSessionsCleared, handleSessionCreated, handleSessionLastSeenUpdated]);

  const handleRevokeSession = (session: Session) => {
    setPendingRevokeSession(session);
  };

  const confirmRevokeSession = async () => {
    if (!pendingRevokeSession) return;

    const isOwnSession =
      (pendingRevokeSession.type === 'authenticated' &&
        pendingRevokeSession.id === authService.getDeviceId()) ||
      (pendingRevokeSession.type === 'guest' &&
        pendingRevokeSession.id === authService.getGuestSessionId());

    try {
      setRevokingSession(pendingRevokeSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingRevokeSession.id)}?action=revoke`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        if (isOwnSession) {
          console.warn('[UserTab] You revoked your own session - forcing logout');
          setPendingRevokeSession(null);
          showToast('info', 'You revoked your own session. Logging out...');

          setTimeout(async () => {
            authService.clearAuth();
            await refreshAuth();
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingRevokeSession(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to revoke session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to revoke session');
    } finally {
      setRevokingSession(null);
    }
  };

  const handleDeleteSession = (session: Session) => {
    setPendingDeleteSession(session);
  };

  const confirmDeleteSession = async () => {
    if (!pendingDeleteSession) return;

    const isOwnSession =
      (pendingDeleteSession.type === 'authenticated' &&
        pendingDeleteSession.id === authService.getDeviceId()) ||
      (pendingDeleteSession.type === 'guest' &&
        pendingDeleteSession.id === authService.getGuestSessionId());

    try {
      setDeletingSession(pendingDeleteSession.id);
      const endpoint = `/api/sessions/${encodeURIComponent(pendingDeleteSession.id)}?action=delete`;

      const response = await fetch(endpoint, {
        method: 'DELETE',
        headers: ApiService.getHeaders()
      });

      if (response.ok) {
        if (isOwnSession) {
          console.warn('[UserTab] You deleted your own session - forcing logout');
          setPendingDeleteSession(null);
          showToast('info', 'You deleted your own session. Logging out...');

          setTimeout(async () => {
            authService.clearAuth();
            await refreshAuth();
          }, 2000);
          return;
        }

        await loadSessions(false);
        setPendingDeleteSession(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.message || errorData.error || 'Failed to delete session');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to delete session');
    } finally {
      setDeletingSession(null);
    }
  };

  const handleEditSession = async (session: Session) => {
    setEditingSession(session);
    setLoadingPreferences(true);
    try {
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(session.id)}`,
        {
          headers: ApiService.getHeaders()
        }
      );

      if (response.ok) {
        const prefs = await response.json();
        const selectedTheme =
          prefs.selectedTheme && prefs.selectedTheme.trim() !== '' ? prefs.selectedTheme : null;
        setEditingPreferences({
          selectedTheme: selectedTheme,
          sharpCorners: prefs.sharpCorners ?? false,
          disableFocusOutlines: prefs.disableFocusOutlines ?? true,
          disableTooltips: prefs.disableTooltips ?? false,
          picsAlwaysVisible: prefs.picsAlwaysVisible ?? false,
          disableStickyNotifications: prefs.disableStickyNotifications ?? false,
          showDatasourceLabels: prefs.showDatasourceLabels ?? true,
          refreshRate: prefs.refreshRate ?? null
        });
      } else {
        setEditingPreferences({
          selectedTheme: null,
          sharpCorners: false,
          disableFocusOutlines: true,
          disableTooltips: false,
          picsAlwaysVisible: false,
          disableStickyNotifications: false,
          showDatasourceLabels: true,
          refreshRate: null
        });
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to load user preferences');
      setEditingSession(null);
    } finally {
      setLoadingPreferences(false);
    }
  };

  const handleSavePreferences = async () => {
    if (!editingSession || !editingPreferences) return;

    try {
      setSavingPreferences(true);
      const response = await fetch(
        `/api/user-preferences/session/${encodeURIComponent(editingSession.id)}`,
        {
          method: 'PUT',
          headers: {
            'Content-Type': 'application/json',
            ...ApiService.getHeaders()
          },
          body: JSON.stringify(editingPreferences)
        }
      );

      if (response.ok) {
        const isOwnSession =
          (editingSession.type === 'authenticated' &&
            editingSession.id === authService.getDeviceId()) ||
          (editingSession.type === 'guest' &&
            editingSession.id === authService.getGuestSessionId());

        if (isOwnSession) {
          if (editingPreferences.selectedTheme) {
            await themeService.setTheme(editingPreferences.selectedTheme);
          }
          await themeService.setSharpCorners(editingPreferences.sharpCorners);
          await themeService.setDisableTooltips(editingPreferences.disableTooltips);
          await themeService.setDisableStickyNotifications(
            editingPreferences.disableStickyNotifications
          );
          await themeService.setPicsAlwaysVisible(editingPreferences.picsAlwaysVisible);

          window.dispatchEvent(
            new CustomEvent('preference-changed', {
              detail: { key: 'showDatasourceLabels', value: editingPreferences.showDatasourceLabels }
            })
          );
        }

        if (editingSession.type === 'guest') {
          await fetch(`/api/sessions/${encodeURIComponent(editingSession.id)}/refresh-rate`, {
            method: 'PATCH',
            headers: {
              'Content-Type': 'application/json',
              ...ApiService.getHeaders()
            },
            body: JSON.stringify({ refreshRate: editingPreferences.refreshRate || '' })
          });
        }

        setEditingSession(null);
        setEditingPreferences(null);
      } else {
        const errorData = await response.json();
        showToast('error', errorData.error || 'Failed to save preferences');
      }
    } catch (err: unknown) {
      showToast('error', getErrorMessage(err) || 'Failed to save preferences');
    } finally {
      setSavingPreferences(false);
    }
  };

  const formatTimeRemaining = (expiresAt: string) => {
    const now = new Date();
    const expiry = new Date(expiresAt);
    const diff = expiry.getTime() - now.getTime();

    if (diff <= 0) return 'Expired';

    const hours = Math.floor(diff / (1000 * 60 * 60));
    const minutes = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));

    if (hours > 0) {
      return `${hours}h ${minutes}m remaining`;
    }
    return `${minutes}m remaining`;
  };

  const isSessionActive = (session: Session) => {
    if (session.isRevoked || session.isExpired) return false;

    if (session.id === currentDeviceId && isLocallyActive) {
      return true;
    }

    if (!session.lastSeenAt) return false;

    const now = new Date();
    const lastSeen = new Date(session.lastSeenAt);
    const diffSeconds = (now.getTime() - lastSeen.getTime()) / 1000;

    return diffSeconds <= 60;
  };

  // Render a single session card
  const renderSessionCard = (session: Session) => {
    const isExpanded = expandedSessions.has(session.id);
    const isActive = isSessionActive(session);
    const isDimmed = session.isExpired || session.isRevoked;

    return (
      <div
        key={session.id}
        className="rounded-lg overflow-hidden"
        style={{
          backgroundColor: isDimmed ? 'var(--theme-bg-tertiary)' : 'var(--theme-bg-secondary)',
          border: '1px solid var(--theme-border)'
        }}
      >
        {/* Header - Always visible */}
        <div
          className="p-3 sm:p-4"
          style={{ opacity: isDimmed ? 0.6 : 1 }}
        >
          {/* Mobile: Clickable header to expand */}
          <div
            className="sm:hidden cursor-pointer"
            onClick={() => toggleSessionExpanded(session.id)}
          >
            {/* Top row: Icon, name, badges */}
            <div className="flex items-start gap-3">
              <div
                className="p-2 rounded-lg flex-shrink-0"
                style={{
                  backgroundColor:
                    session.type === 'authenticated'
                      ? 'var(--theme-user-session-bg)'
                      : 'var(--theme-guest-session-bg)'
                }}
              >
                <User
                  className="w-5 h-5"
                  style={{
                    color:
                      session.type === 'authenticated'
                        ? 'var(--theme-user-session)'
                        : 'var(--theme-guest-session)'
                  }}
                />
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2 flex-wrap mb-1">
                  <h3
                    className="font-semibold truncate text-sm"
                    style={{ color: 'var(--theme-text-primary)' }}
                  >
                    {session.deviceName || 'Unknown Device'}
                  </h3>
                  <span
                    className="px-1.5 py-0.5 text-[10px] rounded font-medium flex-shrink-0"
                    style={{
                      backgroundColor:
                        session.type === 'authenticated'
                          ? 'var(--theme-user-session-bg)'
                          : 'var(--theme-guest-session-bg)',
                      color:
                        session.type === 'authenticated'
                          ? 'var(--theme-user-session)'
                          : 'var(--theme-guest-session)'
                    }}
                  >
                    {session.type === 'authenticated' ? 'USER' : 'GUEST'}
                  </span>
                  {isActive && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium flex-shrink-0"
                      style={{
                        backgroundColor: 'var(--theme-active-session-bg)',
                        color: 'var(--theme-active-session)'
                      }}
                    >
                      Active
                    </span>
                  )}
                </div>
                {/* Summary line */}
                <p className="text-xs truncate" style={{ color: 'var(--theme-text-muted)' }}>
                  {[session.browser, session.operatingSystem].filter(Boolean).join(' · ') || 'Unknown device'}
                </p>
                {/* Status badges */}
                <div className="flex items-center gap-1.5 mt-1.5 flex-wrap">
                  {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-warning-bg)',
                        color: 'var(--theme-warning-text)'
                      }}
                    >
                      {formatTimeRemaining(session.expiresAt)}
                    </span>
                  )}
                  {session.isRevoked && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-error-bg)',
                        color: 'var(--theme-error-text)'
                      }}
                    >
                      Revoked
                    </span>
                  )}
                  {session.isExpired && !session.isRevoked && (
                    <span
                      className="px-1.5 py-0.5 text-[10px] rounded font-medium"
                      style={{
                        backgroundColor: 'var(--theme-warning-bg)',
                        color: 'var(--theme-warning-text)'
                      }}
                    >
                      Expired
                    </span>
                  )}
                </div>
              </div>
              {/* Expand indicator */}
              <ChevronDown
                className={`w-5 h-5 flex-shrink-0 transition-transform duration-200 ${isExpanded ? 'rotate-180' : ''}`}
                style={{ color: 'var(--theme-text-muted)' }}
              />
            </div>
          </div>

          {/* Desktop: Full layout always visible */}
          <div className="hidden sm:block">
            <div className="flex items-start justify-between gap-4">
              <div className="flex items-start gap-3 flex-1 min-w-0">
                <div
                  className="p-2 rounded-lg flex-shrink-0"
                  style={{
                    backgroundColor:
                      session.type === 'authenticated'
                        ? 'var(--theme-user-session-bg)'
                        : 'var(--theme-guest-session-bg)'
                  }}
                >
                  <User
                    className="w-5 h-5"
                    style={{
                      color:
                        session.type === 'authenticated'
                          ? 'var(--theme-user-session)'
                          : 'var(--theme-guest-session)'
                    }}
                  />
                </div>
                <div className="flex-1 min-w-0">
                  {/* Name and badges */}
                  <div className="flex items-center gap-2 flex-wrap mb-2">
                    <h3
                      className="font-semibold truncate"
                      style={{ color: 'var(--theme-text-primary)' }}
                    >
                      {session.deviceName || 'Unknown Device'}
                    </h3>
                    <span
                      className="px-2 py-0.5 text-xs rounded font-medium"
                      style={{
                        backgroundColor:
                          session.type === 'authenticated'
                            ? 'var(--theme-user-session-bg)'
                            : 'var(--theme-guest-session-bg)',
                        color:
                          session.type === 'authenticated'
                            ? 'var(--theme-user-session)'
                            : 'var(--theme-guest-session)'
                      }}
                    >
                      {session.type === 'authenticated' ? 'USER' : 'GUEST'}
                    </span>
                    {isActive && (
                      <span
                        className="px-2 py-0.5 text-xs rounded font-medium"
                        style={{
                          backgroundColor: 'var(--theme-active-session-bg)',
                          color: 'var(--theme-active-session)'
                        }}
                      >
                        Active
                      </span>
                    )}
                    {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                      <span
                        className="px-2 py-0.5 text-xs rounded font-medium"
                        style={{
                          backgroundColor: 'var(--theme-warning-bg)',
                          color: 'var(--theme-warning-text)'
                        }}
                      >
                        {formatTimeRemaining(session.expiresAt)}
                      </span>
                    )}
                    {session.isRevoked && (
                      <span
                        className="px-2 py-0.5 text-xs rounded font-medium"
                        style={{
                          backgroundColor: 'var(--theme-error-bg)',
                          color: 'var(--theme-error-text)'
                        }}
                      >
                        Revoked
                      </span>
                    )}
                    {session.isExpired && !session.isRevoked && (
                      <span
                        className="px-2 py-0.5 text-xs rounded font-medium"
                        style={{
                          backgroundColor: 'var(--theme-warning-bg)',
                          color: 'var(--theme-warning-text)'
                        }}
                      >
                        Expired
                      </span>
                    )}
                  </div>

                  {/* Metadata grid - desktop */}
                  <div className="grid grid-cols-2 lg:grid-cols-3 gap-x-6 gap-y-2 text-sm">
                    {session.ipAddress && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                        <Network className="w-4 h-4 flex-shrink-0" />
                        <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} className="truncate" />
                      </div>
                    )}
                    {session.operatingSystem && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                        <Monitor className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.operatingSystem}</span>
                      </div>
                    )}
                    {session.browser && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                        <Globe className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">{session.browser}</span>
                      </div>
                    )}
                    <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                      <Clock className="w-4 h-4 flex-shrink-0" />
                      <span className="truncate">
                        Created: <FormattedTimestamp timestamp={session.createdAt} />
                      </span>
                    </div>
                    {session.lastSeenAt && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          Last seen: <FormattedTimestamp timestamp={session.lastSeenAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedAt && session.type === 'guest' && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-error-text)' }}>
                        <Clock className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">
                          Revoked: <FormattedTimestamp timestamp={session.revokedAt} />
                        </span>
                      </div>
                    )}
                    {session.revokedBy && session.type === 'guest' && (
                      <div className="flex items-center gap-2" style={{ color: 'var(--theme-text-secondary)' }}>
                        <User className="w-4 h-4 flex-shrink-0" />
                        <span className="truncate">Revoked by: <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} /></span>
                      </div>
                    )}
                  </div>

                  {/* Device ID */}
                  <div
                    className="text-xs font-mono truncate mt-2 pt-2 border-t"
                    style={{
                      color: 'var(--theme-text-muted)',
                      borderColor: 'var(--theme-border-secondary)'
                    }}
                  >
                    Device ID: {session.deviceId || session.id}
                  </div>
                </div>
              </div>

              {/* Desktop action buttons */}
              <div className="flex gap-2 items-start flex-shrink-0">
                <Button
                  variant="default"
                  color="blue"
                  size="sm"
                  leftSection={<Edit className="w-4 h-4" />}
                  onClick={() => handleEditSession(session)}
                >
                  Edit
                </Button>
                {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                  <Button
                    variant="default"
                    color="orange"
                    size="sm"
                    onClick={() => handleRevokeSession(session)}
                    disabled={revokingSession === session.id}
                  >
                    {revokingSession === session.id ? 'Revoking...' : 'Revoke'}
                  </Button>
                )}
                <Button
                  variant="default"
                  color="red"
                  size="sm"
                  leftSection={<Trash2 className="w-4 h-4" />}
                  onClick={() => handleDeleteSession(session)}
                  disabled={deletingSession === session.id}
                  style={
                    isDimmed
                      ? {
                          backgroundColor: 'var(--theme-bg-secondary)',
                          borderColor: 'var(--theme-error)'
                        }
                      : undefined
                  }
                >
                  {deletingSession === session.id ? 'Deleting...' : 'Delete'}
                </Button>
              </div>
            </div>
          </div>
        </div>

        {/* Mobile expanded content */}
        <div
          className={`sm:hidden overflow-hidden transition-all duration-200 ${isExpanded ? 'max-h-[500px] opacity-100' : 'max-h-0 opacity-0'}`}
        >
          <div
            className="px-3 pb-3 space-y-3"
            style={{
              borderTop: '1px solid var(--theme-border-secondary)',
              opacity: isDimmed ? 0.6 : 1
            }}
          >
            {/* Metadata - stacked layout for mobile */}
            <div className="space-y-2.5 pt-3">
              {session.ipAddress && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                    <Network className="w-3 h-3" />
                    <span>IP Address</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                    <ClientIpDisplay clientIp={cleanIpAddress(session.ipAddress)} />
                  </div>
                </div>
              )}
              {session.operatingSystem && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                    <Monitor className="w-3 h-3" />
                    <span>Operating System</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                    {session.operatingSystem}
                  </div>
                </div>
              )}
              {session.browser && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                    <Globe className="w-3 h-3" />
                    <span>Browser</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                    {session.browser}
                  </div>
                </div>
              )}
              <div>
                <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                  <Clock className="w-3 h-3" />
                  <span>Created</span>
                </div>
                <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                  <FormattedTimestamp timestamp={session.createdAt} />
                </div>
              </div>
              {session.lastSeenAt && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                    <Clock className="w-3 h-3" />
                    <span>Last Seen</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                    <FormattedTimestamp timestamp={session.lastSeenAt} />
                  </div>
                </div>
              )}
              {session.revokedAt && session.type === 'guest' && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-error-text)' }}>
                    <Clock className="w-3 h-3" />
                    <span>Revoked</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-error-text)' }}>
                    <FormattedTimestamp timestamp={session.revokedAt} />
                  </div>
                </div>
              )}
              {session.revokedBy && session.type === 'guest' && (
                <div>
                  <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                    <User className="w-3 h-3" />
                    <span>Revoked By</span>
                  </div>
                  <div className="text-sm font-medium pl-[18px]" style={{ color: 'var(--theme-text-primary)' }}>
                    <ClientIpDisplay clientIp={cleanIpAddress(session.revokedBy)} />
                  </div>
                </div>
              )}
              <div>
                <div className="flex items-center gap-1.5 text-[10px] mb-0.5" style={{ color: 'var(--theme-text-muted)' }}>
                  <span>Device ID</span>
                </div>
                <div className="text-xs font-mono break-all" style={{ color: 'var(--theme-text-secondary)' }}>
                  {session.deviceId || session.id}
                </div>
              </div>
            </div>

            {/* Mobile action buttons */}
            <div className="flex flex-col gap-2 pt-2">
              <Button
                variant="default"
                color="blue"
                size="sm"
                leftSection={<Edit className="w-4 h-4" />}
                onClick={(e) => {
                  e.stopPropagation();
                  handleEditSession(session);
                }}
                fullWidth
              >
                Edit Preferences
              </Button>
              {session.type === 'guest' && !session.isRevoked && !session.isExpired && (
                <Button
                  variant="default"
                  color="orange"
                  size="sm"
                  onClick={(e) => {
                    e.stopPropagation();
                    handleRevokeSession(session);
                  }}
                  disabled={revokingSession === session.id}
                  fullWidth
                >
                  {revokingSession === session.id ? 'Revoking...' : 'Revoke Session'}
                </Button>
              )}
              <Button
                variant="default"
                color="red"
                size="sm"
                leftSection={<Trash2 className="w-4 h-4" />}
                onClick={(e) => {
                  e.stopPropagation();
                  handleDeleteSession(session);
                }}
                disabled={deletingSession === session.id}
                fullWidth
              >
                {deletingSession === session.id ? 'Deleting...' : 'Delete Session'}
              </Button>
            </div>
          </div>
        </div>
      </div>
    );
  };

  return (
    <div className="container mx-auto px-3 sm:px-4 py-4 sm:py-6 space-y-4 sm:space-y-6">
      {/* Header */}
      <div className="flex items-start justify-between gap-3">
        <div className="flex items-center gap-3 min-w-0 flex-1">
          <div
            className="p-2 rounded-lg flex-shrink-0"
            style={{ backgroundColor: 'var(--theme-primary-subtle)' }}
          >
            <Users className="w-6 h-6" style={{ color: 'var(--theme-primary)' }} />
          </div>
          <div className="min-w-0">
            <h1
              className="text-xl sm:text-2xl font-bold"
              style={{ color: 'var(--theme-text-primary)' }}
            >
              User Management
            </h1>
            <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
              Manage all users and devices
            </p>
          </div>
        </div>
        <button
          onClick={() => loadSessions(true)}
          disabled={loading}
          className="p-2 rounded-lg transition-colors disabled:opacity-50 flex items-center justify-center flex-shrink-0"
          style={{
            color: 'var(--theme-text-muted)',
            backgroundColor: 'transparent'
          }}
          onMouseEnter={(e) =>
            !loading && (e.currentTarget.style.backgroundColor = 'var(--theme-bg-hover)')
          }
          onMouseLeave={(e) => (e.currentTarget.style.backgroundColor = 'transparent')}
          title="Refresh devices"
        >
          {loading ? (
            <Loader2 className="w-4 h-4 animate-spin" />
          ) : (
            <RefreshCw className="w-4 h-4" />
          )}
        </button>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-3 gap-2 sm:gap-4">
        <Card>
          <div className="p-3 sm:p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Total
                </p>
                <p className="text-xl sm:text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.length}
                </p>
              </div>
              <Users className="w-6 h-6 sm:w-8 sm:h-8 hidden sm:block" style={{ color: 'var(--theme-primary)' }} />
            </div>
          </div>
        </Card>
        <Card>
          <div className="p-3 sm:p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Users
                </p>
                <p className="text-xl sm:text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter((s) => s.type === 'authenticated').length}
                </p>
              </div>
              <User className="w-6 h-6 sm:w-8 sm:h-8 hidden sm:block" style={{ color: 'var(--theme-user-session)' }} />
            </div>
          </div>
        </Card>
        <Card>
          <div className="p-3 sm:p-4">
            <div className="flex items-center justify-between">
              <div>
                <p className="text-xs sm:text-sm" style={{ color: 'var(--theme-text-secondary)' }}>
                  Guests
                </p>
                <p className="text-xl sm:text-2xl font-bold" style={{ color: 'var(--theme-text-primary)' }}>
                  {sessions.filter((s) => s.type === 'guest').length}
                </p>
              </div>
              <User className="w-6 h-6 sm:w-8 sm:h-8 hidden sm:block" style={{ color: 'var(--theme-guest-session)' }} />
            </div>
          </div>
        </Card>
      </div>

      {/* Sessions List */}
      <Card>
        <div className="p-4 sm:p-6">
          <div className="flex items-center justify-between mb-4">
            <div className="flex items-center gap-2">
              <h2 className="text-lg font-semibold" style={{ color: 'var(--theme-text-primary)' }}>
                All Sessions
              </h2>
              <HelpPopover
                width={300}
                sections={[
                  {
                    title: 'Session Types',
                    items: [
                      { label: 'Authenticated', description: 'Full access, no expiration', color: 'var(--theme-user-session)' },
                      { label: 'Guest', description: `Read-only for ${guestDurationHours} hours`, color: 'var(--theme-guest-session)' }
                    ]
                  },
                  {
                    title: 'Actions',
                    items: [
                      { label: 'Revoke', description: 'End a guest session immediately', color: 'var(--theme-warning)' },
                      { label: 'Delete', description: 'Remove device from history', color: 'var(--theme-error)' }
                    ]
                  }
                ]}
              />
            </div>

            <ToggleSwitch
              options={[
                { value: 'unlocked', label: 'Unlocked', icon: <Unlock />, activeColor: 'success' },
                { value: 'locked', label: 'Locked', icon: <Lock />, activeColor: 'error' }
              ]}
              value={guestModeLocked ? 'locked' : 'unlocked'}
              onChange={handleToggleGuestLock}
              disabled={updatingGuestLock}
              loading={updatingGuestLock}
              title={guestModeLocked ? 'Guest mode is locked - new guests cannot log in' : 'Guest mode is unlocked - guests can log in'}
            />
          </div>

          {loading && (
            <div className="text-center py-8">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p className="text-sm mt-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading sessions...
              </p>
            </div>
          )}

          {!loading && sessions.length === 0 && (
            <div className="text-center py-8">
              <Users
                className="w-12 h-12 mx-auto mb-2"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p style={{ color: 'var(--theme-text-secondary)' }}>No active sessions</p>
            </div>
          )}

          {!loading && sessions.length > 0 && (
            <div className="space-y-3">
              {sessions.map(renderSessionCard)}
            </div>
          )}

          {/* Pagination */}
          {!loading && (
            <div className="mt-6 border-t pt-4" style={{ borderColor: 'var(--theme-border)' }}>
              <Pagination
                currentPage={currentPage}
                totalPages={totalPages}
                totalItems={totalCount}
                itemsPerPage={pageSize}
                onPageChange={(newPage) => {
                  setCurrentPage(newPage);
                  loadSessions(true, newPage);
                }}
                itemLabel="sessions"
                showCard={false}
              />
            </div>
          )}
        </div>
      </Card>

      {/* Guest Device Configuration */}
      <Card>
        <div className="p-4 sm:p-6">
          <h3
            className="text-lg font-semibold mb-4 flex items-center gap-2"
            style={{ color: 'var(--theme-text-primary)' }}
          >
            <Clock className="w-5 h-5" />
            Guest Device Configuration
          </h3>
          <div className="space-y-4">
            <div>
              <label
                className="block text-sm font-medium mb-2"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                Guest Device Duration
              </label>
              <div className="flex items-center gap-3">
                <EnhancedDropdown
                  options={durationOptions}
                  value={guestDurationHours.toString()}
                  onChange={(value) => handleUpdateDuration(Number(value))}
                  disabled={updatingDuration}
                  className="w-full sm:w-64"
                />
                {updatingDuration && (
                  <Loader2
                    className="w-4 h-4 animate-spin"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>
              <p className="text-xs mt-2" style={{ color: 'var(--theme-text-muted)' }}>
                How long guest devices remain valid before expiring
              </p>
            </div>

            <div>
              <label
                className="block text-sm font-medium mb-2"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                Default Guest Theme
              </label>
              <div className="flex items-center gap-3">
                <EnhancedDropdown
                  options={availableThemes.map((theme) => ({
                    value: theme.id,
                    label: theme.name
                  }))}
                  value={defaultGuestTheme}
                  onChange={handleUpdateGuestTheme}
                  disabled={updatingGuestTheme}
                  className="w-full sm:w-64"
                />
                {updatingGuestTheme && (
                  <Loader2
                    className="w-4 h-4 animate-spin"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>
              <p className="text-xs mt-2" style={{ color: 'var(--theme-text-muted)' }}>
                Default theme applied to all guest users
              </p>
            </div>

            <div>
              <label
                className="block text-sm font-medium mb-2"
                style={{ color: 'var(--theme-text-primary)' }}
              >
                Default Guest Refresh Rate
              </label>
              <div className="flex items-center gap-3">
                <EnhancedDropdown
                  options={refreshRateOptions}
                  value={defaultGuestRefreshRate}
                  onChange={handleUpdateGuestRefreshRate}
                  disabled={updatingGuestRefreshRate}
                  className="w-full sm:w-64"
                />
                {updatingGuestRefreshRate && (
                  <Loader2
                    className="w-4 h-4 animate-spin"
                    style={{ color: 'var(--theme-primary)' }}
                  />
                )}
              </div>
              <p className="text-xs mt-2" style={{ color: 'var(--theme-text-muted)' }}>
                Default refresh rate for all guest users
              </p>
            </div>
          </div>
        </div>
      </Card>

      {/* Revoke Device Modal */}
      <Modal
        opened={!!pendingRevokeSession}
        onClose={() => {
          if (!revokingSession) {
            setPendingRevokeSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <AlertTriangle className="w-6 h-6 text-themed-warning" />
            <span>Revoke Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to revoke this{' '}
            {pendingRevokeSession?.type === 'authenticated' ? 'authenticated user' : 'guest'}?
          </p>

          {pendingRevokeSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingRevokeSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingRevokeSession.id}
              </p>
            </div>
          )}

          <Alert color="yellow">
            <div>
              <p className="text-sm font-medium mb-2">What happens when you revoke:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>The device is marked as revoked but not deleted</li>
                <li>The user will be logged out immediately</li>
                <li>The device record remains in history</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingRevokeSession(null)}
              disabled={!!revokingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="orange"
              onClick={confirmRevokeSession}
              loading={!!revokingSession}
            >
              Revoke Device
            </Button>
          </div>
        </div>
      </Modal>

      {/* Delete Device Modal */}
      <Modal
        opened={!!pendingDeleteSession}
        onClose={() => {
          if (!deletingSession) {
            setPendingDeleteSession(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Trash2 className="w-6 h-6 text-themed-error" />
            <span>Delete Device</span>
          </div>
        }
        size="md"
      >
        <div className="space-y-4">
          <p className="text-themed-secondary">
            Are you sure you want to permanently delete this{' '}
            {pendingDeleteSession?.type === 'authenticated'
              ? 'authenticated device'
              : 'guest device'}
            ?
          </p>

          {pendingDeleteSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {pendingDeleteSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted font-mono">
                Device ID: {pendingDeleteSession.id}
              </p>
            </div>
          )}

          <Alert color="red">
            <div>
              <p className="text-sm font-medium mb-2">Warning:</p>
              <ul className="list-disc list-inside text-sm space-y-1 ml-2">
                <li>This action cannot be undone</li>
                <li>The device will be permanently removed from history</li>
                <li>The user will be logged out immediately</li>
              </ul>
            </div>
          </Alert>

          <div className="flex justify-end space-x-3 pt-2">
            <Button
              variant="default"
              onClick={() => setPendingDeleteSession(null)}
              disabled={!!deletingSession}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="red"
              leftSection={<Trash2 className="w-4 h-4" />}
              onClick={confirmDeleteSession}
              loading={!!deletingSession}
            >
              Delete Permanently
            </Button>
          </div>
        </div>
      </Modal>

      {/* Edit User Preferences Modal */}
      <Modal
        opened={!!editingSession}
        onClose={() => {
          if (!savingPreferences) {
            setEditingSession(null);
            setEditingPreferences(null);
          }
        }}
        title={
          <div className="flex items-center space-x-3">
            <Edit className="w-6 h-6 text-themed-accent" />
            <span>Edit User Preferences</span>
          </div>
        }
        size="lg"
      >
        <div className="space-y-4">
          {editingSession && (
            <div className="p-3 rounded-lg bg-themed-tertiary space-y-1">
              <p className="text-sm text-themed-primary font-medium">
                {editingSession.deviceName || 'Unknown Device'}
              </p>
              <p className="text-xs text-themed-muted">
                {editingSession.type === 'authenticated' ? 'Authenticated User' : 'Guest User'}
              </p>
              <p className="text-xs text-themed-muted font-mono">Device ID: {editingSession.id}</p>
            </div>
          )}

          {loadingPreferences && (
            <div className="text-center py-8">
              <Loader2
                className="w-8 h-8 animate-spin mx-auto"
                style={{ color: 'var(--theme-text-muted)' }}
              />
              <p className="text-sm mt-2" style={{ color: 'var(--theme-text-secondary)' }}>
                Loading preferences...
              </p>
            </div>
          )}

          {!loadingPreferences && editingPreferences && (
            <div className="space-y-4">
              {/* Theme Selection */}
              <div>
                <label className="block text-sm font-medium text-themed-primary mb-2">
                  Selected Theme
                </label>
                <EnhancedDropdown
                  options={[
                    {
                      value: 'default',
                      label: `Default Theme (${availableThemes.find((t) => t.id === defaultGuestTheme)?.name || defaultGuestTheme})`
                    },
                    ...availableThemes.map((theme) => ({
                      value: theme.id,
                      label: theme.name
                    }))
                  ]}
                  value={
                    !editingPreferences.selectedTheme ? 'default' : editingPreferences.selectedTheme
                  }
                  onChange={(value) =>
                    setEditingPreferences({
                      ...editingPreferences,
                      selectedTheme: value === 'default' ? null : value
                    })
                  }
                  className="w-full"
                />
              </div>

              {/* Refresh Rate (Guest Users Only) */}
              {editingSession && editingSession.type === 'guest' && (
                <div>
                  <label className="block text-sm font-medium text-themed-primary mb-2">
                    Refresh Rate
                  </label>
                  <EnhancedDropdown
                    options={[
                      {
                        value: 'default',
                        label: `Default (${refreshRateOptions.find((o) => o.value === defaultGuestRefreshRate)?.label || defaultGuestRefreshRate})`
                      },
                      ...refreshRateOptions
                    ]}
                    value={editingPreferences.refreshRate || 'default'}
                    onChange={(value) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        refreshRate: value === 'default' ? null : value
                      })
                    }
                    className="w-full"
                  />
                  <p className="text-xs text-themed-muted mt-1">
                    Controls how often this guest&apos;s dashboard refreshes data
                  </p>
                </div>
              )}

              {/* UI Preferences */}
              <div className="space-y-3">
                <h4 className="text-sm font-medium text-themed-primary">UI Preferences</h4>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.sharpCorners}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        sharpCorners: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Sharp Corners</span>
                </label>

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={!editingPreferences.disableTooltips}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        disableTooltips: !e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <span className="text-sm text-themed-secondary">Tooltips</span>
                </label>

                {editingSession && editingSession.type === 'authenticated' && (
                  <>
                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={!editingPreferences.disableStickyNotifications}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            disableStickyNotifications: !e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Sticky Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Keep notification bar fixed at top when scrolling
                        </span>
                      </div>
                    </label>

                    <label className="flex items-center gap-3 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={editingPreferences.picsAlwaysVisible}
                        onChange={(e) =>
                          setEditingPreferences({
                            ...editingPreferences,
                            picsAlwaysVisible: e.target.checked
                          })
                        }
                        className="w-4 h-4 rounded"
                        style={{ accentColor: 'var(--theme-primary)' }}
                      />
                      <div className="flex flex-col">
                        <span className="text-sm text-themed-secondary">Static Notifications</span>
                        <span className="text-xs text-themed-muted">
                          Require manual dismissal - won&apos;t auto-clear
                        </span>
                      </div>
                    </label>
                  </>
                )}

                <label className="flex items-center gap-3 cursor-pointer">
                  <input
                    type="checkbox"
                    checked={editingPreferences.showDatasourceLabels}
                    onChange={(e) =>
                      setEditingPreferences({
                        ...editingPreferences,
                        showDatasourceLabels: e.target.checked
                      })
                    }
                    className="w-4 h-4 rounded"
                    style={{ accentColor: 'var(--theme-primary)' }}
                  />
                  <div className="flex flex-col">
                    <span className="text-sm text-themed-secondary">Datasource Labels</span>
                    <span className="text-xs text-themed-muted">
                      Show datasource indicators on downloads (multi-datasource mode)
                    </span>
                  </div>
                </label>
              </div>
            </div>
          )}

          <div
            className="flex justify-end space-x-3 pt-4 border-t"
            style={{ borderColor: 'var(--theme-border-secondary)' }}
          >
            <Button
              variant="default"
              onClick={() => {
                setEditingSession(null);
                setEditingPreferences(null);
              }}
              disabled={savingPreferences}
            >
              Cancel
            </Button>
            <Button
              variant="filled"
              color="blue"
              onClick={handleSavePreferences}
              loading={savingPreferences}
              disabled={loadingPreferences}
            >
              Save Preferences
            </Button>
          </div>
        </div>
      </Modal>
    </div>
  );
};

export default UserTab;
